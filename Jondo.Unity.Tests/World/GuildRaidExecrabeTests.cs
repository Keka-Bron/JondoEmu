using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Cangrancio's fight: four thresholds, at each one form of the four, never repeated, and
    /// the order kept on the raid for its statues. And the casts the server picks among a list.
    /// </summary>
    [Collection("guild raids")]
    public class GuildRaidExecrabeTests : IDisposable
    {
        private readonly string _file;

        public GuildRaidExecrabeTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-raidexe-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
            GuildRaidBoard.Forget();
            GuildRaidManager.Forget();
        }

        public void Dispose()
        {
            GuildRaidManager.Forget();
            GuildRaidBoard.Forget();
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static async Task<RaidInstance> RunningWith(long member)
        {
            var guild = GuildStore.Create(member, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.SpendGuildKamas(guild.Id, -1000);
            var raid = await GuildRaidManager.LaunchAsync(GuildRaidBoard.Purchase(member, Raids.Gigalodon).Raid);
            raid.Add(member);
            return raid;
        }

        private static FightInstance FightOf(long fightId, long player)
        {
            var fight = new FightInstance(fightId, 1) { FightId = fightId };
            fight.AddPlayer(new Fighter { Id = player, TeamId = 0, MaxHP = 100, CurrentHP = 100 });
            fight.AddMonster(new Fighter { Id = -1, TeamId = 1, MaxHP = 100000, CurrentHP = 100000, IsMonster = true,
                                           MonsterId = GuildRaidExecrabe.Boss });
            return fight;
        }

        /// <summary>Each form spell puts its phase state, at every threshold.</summary>
        [Fact]
        public void The_form_spells_say_their_form()
        {
            Assert.Equal(6724, GuildRaidExecrabe.FormOf(32224));      // Oursin, Check 1
            Assert.Equal(6725, GuildRaidExecrabe.FormOf(32685));      // Coquillage, Check 2
            Assert.Equal(6726, GuildRaidExecrabe.FormOf(32689));      // Perle, Check 3
            Assert.Equal(6727, GuildRaidExecrabe.FormOf(32693));      // Poulpe, Check 4
            Assert.Equal(0, GuildRaidExecrabe.FormOf(32231));         // Encornage, an attack
        }

        /// <summary>One form per threshold, four different ones, and the raid keeps their order.</summary>
        [Fact]
        public async Task Each_threshold_takes_one_new_form()
        {
            var raid = await RunningWith(7001);
            var fight = FightOf(501, 7001);
            var dice = new Random(9);

            var chosen = GuildRaidExecrabe.ThresholdSpells.Select(threshold =>
            {
                var candidates = SpellEffects.De(threshold, 1)
                                             .Where(e => e.EffectId == EffectEngine.EfectoQueLanzaHechizo).Select(e => e.DiceNum).ToList();
                var allowed = candidates.Where(c => GuildRaidExecrabe.Allows(fight, threshold, c, dice)).ToList();
                Assert.Single(allowed);
                return GuildRaidExecrabe.FormOf(allowed[0]);
            }).ToList();

            Assert.Equal(GuildRaidExecrabe.Forms.OrderBy(f => f), chosen.OrderBy(f => f));
            Assert.Equal(chosen, raid.SequenceOf(GuildRaidExecrabe.OrderSequence));
            GuildRaidExecrabe.Forget(fight.FightId);
        }

        /// <summary>Through the engine: a threshold crossed chains exactly one form spell.</summary>
        [Fact]
        public async Task The_engine_chains_one_form_per_threshold()
        {
            await RunningWith(7001);
            var fight = FightOf(502, 7001);
            var boss = fight.Rojo[0];
            var outcomes = EffectEngine.Resolver(fight, boss, GuildRaidExecrabe.ThresholdSpells[0], 1, boss,
                                                 EffectEngine.AlLanzar, 1, celdaApuntada: boss.CellId);
            var forms = outcomes.Where(o => GuildRaidExecrabe.FormOf(o.HechizoEncadenado) != 0).ToList();
            Assert.Single(forms);
            GuildRaidExecrabe.Forget(fight.FightId);
        }

        /// <summary>Pensamientos Oscuros casts only the fight's tier; without one, none.</summary>
        [Fact]
        public void The_darkness_dispatcher_casts_only_the_fight_s_tier()
        {
            var fight = new FightInstance(1, 1) { DarknessTier = 32746 };
            Assert.True(ChosenCasts.Allows(fight, GuildRaidDarkness.DarkThoughts, 32746));
            Assert.False(ChosenCasts.Allows(fight, GuildRaidDarkness.DarkThoughts, 32742));
            Assert.False(ChosenCasts.Allows(new FightInstance(2, 1), GuildRaidDarkness.DarkThoughts, 32746));
            Assert.True(ChosenCasts.Allows(fight, 32199, 32201));       // any other spell casts as ever
        }

        /// <summary>Beating the Cangrancio meets nothing: its goal is the statues'.</summary>
        [Fact]
        public void The_cangrancio_s_goal_is_not_a_kill()
        {
            var goal = GuildRaidExecrabe.EnigmaGoal(Raids.Gigalodon);
            Assert.Equal(16, goal.Id);
            Assert.Equal(5, goal.UnlocksFloor);
            Assert.False(GuildRaidManager.KillGoals(Raids.Gigalodon).ContainsKey(GuildRaidExecrabe.Boss));
            Assert.True(GuildRaidManager.KillGoals(Raids.Gigalodon).ContainsKey(8333));      // the Morreina's still is
        }
    }
}
