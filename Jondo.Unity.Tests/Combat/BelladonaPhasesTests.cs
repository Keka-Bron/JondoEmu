using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Belladona (7210), the Cámara de los Maleficios' boss. Her phase 2 (23793) takes her
    /// invulnerability (24115, state 56) away at the end of that turn; her threshold at half life
    /// (23795) brings her minions back and casts her phase 1 again (23791), invulnerable once more.
    /// The turn-end dispel of phase 2 is for that turn only: it must not strip the phase-3
    /// invulnerability at her next turn end.
    /// </summary>
    [Collection("MapManager")]
    public class BelladonaPhasesTests
    {
        private const int Phase2 = 23793;
        private const int Invulnerability = 24115;
        private const int Invulnerable = 56;

        private static Fighter Player(int cell) => new()
        {
            Id = 9_970_401, Name = "P", CellId = cell, Level = 200, MaxHP = 5000, CurrentHP = 5000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
        };

        private static Fighter Belladona(int cell) => new()
        {
            Id = -1, Name = "Belladona", IsMonster = true, CellId = cell, Level = 220, MaxHP = 33000,
            CurrentHP = 33000, MaxAP = 12, CurrentAP = 12, MaxMP = 0, CurrentMP = 0, MonsterId = 7210,
        };

        [Fact]
        public async Task The_phase_2_dispel_does_not_outlive_its_turn()
        {
            var player = Player(407);
            var boss = Belladona(165);
            var fight = new FightInstance(7210, 990_007_210);
            fight.AddPlayer(player);
            player.CellId = 407;
            fight.AddMonster(boss);
            boss.CellId = 165;

            await FightHandler.AplicarEfectosAsync(null, fight, boss, Invulnerability, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            Assert.Contains(Invulnerable, boss.Buffs.Estados);

            // Phase 2: vulnerable once the turn it starts in is over.
            await FightHandler.AplicarEfectosAsync(null, fight, boss, Phase2, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlAcabarElTurno);
            Assert.DoesNotContain(Invulnerable, boss.Buffs.Estados);

            // Some rounds later the threshold casts phase 1 again: invulnerable through her turn ends.
            for (int turn = 0; turn < 6; turn++) fight.NextTurn();
            await FightHandler.AplicarEfectosAsync(null, fight, boss, Invulnerability, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            Assert.Contains(Invulnerable, boss.Buffs.Estados);
            for (int turn = 0; turn < 4; turn++)
            {
                fight.NextTurn();
                await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlAcabarElTurno);
            }
            Assert.Contains(Invulnerable, boss.Buffs.Estados);
        }
    }
}
