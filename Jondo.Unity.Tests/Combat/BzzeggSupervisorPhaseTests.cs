using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Bzzegg Supervisor (8052) climbs his phases -- 6276, 6277, 6278, 6279, and at 6279 his
    /// 31079 casts 31081 grade 2, which takes his invulnerability off -- each time a Bzzegg bursts
    /// within two cells of him. The Kabombz lay those eggs (31036, 31069, 31072, 31075), and an egg
    /// bursts from its 31031: when it dies it casts 31032 grade 2, whose rows cast nothing unless
    /// the egg carries a season. The season is put on every fighter when the fight begins;
    /// an egg laid later has to be given it, or the Supervisor never leaves his first phase.
    /// </summary>
    [Collection("MapManager")]
    public class BzzeggSupervisorPhaseTests
    {
        private const int Supervisor = 8052, Kamikabz = 8050, KamikabzEgg = 8055, FirstPhase = 6276, Watching = 6274;

        /// <summary>A map of Osabana, its own arena.</summary>
        private const long Osabana = 234618880;

        private static bool HasData => SpellEffects.De(31032, 1).Count > 0 && SpellEffects.De(31081, 1).Count > 0
                                       && SpellEffects.De(31064, 2).Count > 0;

        private static (FightInstance Fight, Fighter Supervisor, Fighter Kamikabz) Fight()
        {
            if (!MapManager.WalkableCells.ContainsKey(Osabana)) MapManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            var fight = new FightInstance(9931, Osabana, Osabana);
            fight.AddPlayer(new Fighter { Id = 7411, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200, Initiative = 1 });
            var boss = new Fighter { Id = -1, TeamId = 1, CellId = 314, MaxHP = 9000, CurrentHP = 9000, IsMonster = true,
                                     MonsterId = Supervisor, Level = 170, Initiative = 100 };
            var layer = new Fighter { Id = -2, TeamId = 1, CellId = 342, MaxHP = 3000, CurrentHP = 3000, IsMonster = true,
                                      MonsterId = Kamikabz, Level = 166, Initiative = 90 };
            fight.AddMonster(boss);
            fight.AddMonster(layer);
            fight.SceneStates.Add(OsavoraFights.ChasseState);
            fight.StartFight();
            foreach (var f in new[] { boss, layer }) f.Buffs.PonerEstado(OsavoraFights.ChasseState);
            boss.Buffs.PonerEstado(Watching);
            return (fight, boss, layer);
        }

        [Fact]
        public async Task An_egg_laid_in_the_fight_carries_the_scene_s_season()
        {
            if (!HasData) return;
            var (fight, boss, layer) = Fight();
            int cell = MapGeometry.GetNeighbors(boss.CellId).First(c => fight.Todos.All(f => f.CellId != c));

            var egg = await FightHandler.SummonAsync(null, fight, layer, KamikabzEgg, 1, cell);

            Assert.NotNull(egg);
            Assert.True(egg.Buffs.TieneEstado(OsavoraFights.ChasseState));
            Assert.False(egg.Buffs.TieneEstado(OsavoraFights.ReposState));
        }

        [Fact]
        public async Task An_egg_killed_by_the_supervisor_moves_him_to_his_first_phase()
        {
            if (!HasData) return;
            var (fight, boss, layer) = Fight();
            int cell = MapGeometry.GetNeighbors(boss.CellId).First(c => fight.Todos.All(f => f.CellId != c));
            var egg = await FightHandler.SummonAsync(null, fight, layer, KamikabzEgg, 1, cell);
            Assert.False(boss.Buffs.TieneEstado(FirstPhase));

            // What its 31031 casts when it dies ("X"): 31032 grade 2, the burst of the season.
            await FightHandler.AplicarEfectosAsync(null, fight, egg, 31032, 2, egg, EffectEngine.AlLanzar, egg.CellId);

            Assert.True(boss.Buffs.TieneEstado(FirstPhase));
        }
    }
}
