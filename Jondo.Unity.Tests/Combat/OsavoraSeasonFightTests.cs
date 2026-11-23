using System;
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
    /// What Osavora's seasons change in its monsters: the scene's season, which each monster's
    /// starting spell copies and nothing else, and what the season gives it.
    /// </summary>
    [Collection("MapManager")]
    public class OsavoraSeasonFightTests
    {
        private const int Felifiera = 8038;

        /// <summary>A map of Osabana, its own arena.</summary>
        private const long Osabana = 234618880;

        private static bool HasData => SpellEffects.De(30910, 1).Count > 0 && SpellEffects.De(30991, 2).Count > 0;

        private static async Task<(FightInstance Fight, Fighter Monster)> Fight(int seasonState)
        {
            if (!MapManager.WalkableCells.ContainsKey(Osabana)) MapManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            var fight = new FightInstance(9901 + seasonState, Osabana, Osabana);
            fight.AddPlayer(new Fighter { Id = 7401, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200, Initiative = 1 });
            var monster = new Fighter { Id = -1, TeamId = 1, CellId = 314, MaxHP = 8200, CurrentHP = 8200, IsMonster = true,
                                        MonsterId = Felifiera, Level = 200, Initiative = 100 };
            fight.AddMonster(monster);
            fight.SceneStates.Add(seasonState);
            fight.StartFight();
            monster.Conducta = (30910, 1);   // its grade's startingSpellId, 81692
            await FightHandler.LanzarLaConductaAsync(null, fight, monster);
            return (fight, monster);
        }

        [Theory]
        [InlineData(OsavoraFights.NaissanceState)]
        [InlineData(OsavoraFights.ChasseState)]
        [InlineData(OsavoraFights.ReposState)]
        public async Task A_monster_carries_the_scene_s_season_and_no_other(int season)
        {
            if (!HasData) return;
            var (_, monster) = await Fight(season);

            var seasons = new[] { OsavoraFights.NaissanceState, OsavoraFights.ChasseState, OsavoraFights.ReposState };
            Assert.Equal(new[] { season }, seasons.Where(monster.Buffs.TieneEstado));
        }

        [Fact]
        public async Task The_birth_season_gives_its_monsters_a_movement_point()
        {
            if (!HasData) return;
            var (_, born) = await Fight(OsavoraFights.NaissanceState);
            var (_, hunting) = await Fight(OsavoraFights.ChasseState);

            // 31293: «128 on C,E6245» -- +1 MP in the Naissance only.
            Assert.Contains(born.Buffs.Puestos, b => b.EffectId == 128);
            Assert.DoesNotContain(hunting.Buffs.Puestos, b => b.EffectId == 128);
        }

        /// <summary>
        /// Every fifth blow its counter (30901: 6340, 6341, 6155, 6156, 6157) reaches 6157, whose
        /// coming into force ("EACT6157") casts the season's reaction, which takes 6157 off again.
        /// </summary>
        [Fact]
        public async Task The_fifth_blow_sets_off_the_season_s_reaction()
        {
            if (!HasData) return;
            var (fight, monster) = await Fight(OsavoraFights.ChasseState);
            int[] counter = { 6340, 6341, 6155, 6156, 6157 };

            for (int blow = 1; blow <= 4; blow++)
                await FightHandler.DispararAsync(null, fight, monster, EffectEngine.AlRecibirDano);
            Assert.True(monster.Buffs.TieneEstado(6156));

            await FightHandler.DispararAsync(null, fight, monster, EffectEngine.AlRecibirDano);
            Assert.DoesNotContain(counter, monster.Buffs.TieneEstado);
        }

        [Fact]
        public void Only_osavora_s_fights_have_a_season()
        {
            var clock = OsavoraFights.Clock;
            Interactives.Initialize();
            try
            {
                // Monday 21 July 2025, 9:00 in Paris: the first week's Naissance.
                OsavoraFights.Clock = () => new DateTimeOffset(2025, 7, 21, 7, 0, 0, TimeSpan.Zero);
                var there = new FightInstance(9911, 0, 0);
                Assert.Equal(31050, OsavoraFights.SetTheScene(there, OsavoraFights.Osabana));
                Assert.Equal(new[] { OsavoraFights.NaissanceState }, there.SceneStates);

                if (Interactives.AreaOfSubArea(1044) == OsavoraFights.Osavora)
                {
                    // The Bzzegg Supervisor's Checkpoint: its Kabombz need the season to hatch.
                    var checkpoint = new FightInstance(9913, 0, 0);
                    Assert.Equal(31050, OsavoraFights.SetTheScene(checkpoint, 1044));
                    Assert.Equal(new[] { OsavoraFights.NaissanceState }, checkpoint.SceneStates);
                }

                var elsewhere = new FightInstance(9912, 0, 0);
                Assert.Equal(0, OsavoraFights.SetTheScene(elsewhere, 1));
                Assert.Empty(elsewhere.SceneStates);
            }
            finally
            {
                OsavoraFights.Clock = clock;
            }
        }
    }
}
