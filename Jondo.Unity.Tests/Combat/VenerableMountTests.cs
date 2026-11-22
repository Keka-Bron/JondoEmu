using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Venerable Durmiente (8047), the Coto del Venerable's boss. His starting spell (31108)
    /// puts him to sleep (31356): invulnerable (state 56), with his mount (8120, 31359) beside him.
    /// The mount's death (its 31357, trigger X) casts 31358 on him, which takes the sleep away
    /// and leaves him awake for good (state 6484).
    /// </summary>
    [Collection("MapManager")]
    public class VenerableMountTests
    {
        private const long Coto = 236454912;
        private const int Venerable = 8047;
        private const int Mount = 8120;
        private const int Invulnerable = 56;
        private const int AwakeForGood = 6484;

        private static bool HasData => SpellEffects.De(31108, 1).Count > 0 && SpellEffects.De(31358, 2).Count > 0;

        [Fact]
        public async Task The_mount_s_death_wakes_the_boss_for_good()
        {
            if (!HasData) return;
            if (!MapManager.WalkableCells.ContainsKey(Coto)) MapManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            var fight = new FightInstance(9947, Coto, Coto);
            var player = new Fighter { Id = 7501, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200, Initiative = 1 };
            fight.AddPlayer(player);
            var boss = new Fighter { Id = -1, TeamId = 1, CellId = 314, MaxHP = 32000, CurrentHP = 32000, IsMonster = true,
                                     MonsterId = Venerable, Level = 220, Initiative = 100 };
            fight.AddMonster(boss);
            fight.StartFight();
            boss.Conducta = (31108, 1);   // its grade's startingSpellId, 82223
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);

            Assert.Contains(Invulnerable, boss.Buffs.Estados);
            var mount = Assert.Single(fight.Todos, f => f.IsAlive && f.MonsterId == Mount);

            await FightHandler.TestKillAsync(null, fight, mount, player);

            Assert.DoesNotContain(Invulnerable, boss.Buffs.Estados);
            Assert.Contains(AwakeForGood, boss.Buffs.Estados);
        }
    }
}
