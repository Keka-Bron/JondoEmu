using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Gigalodón's "Pensamientos Oscuros": the darker a floor, the stronger the monsters bound
    /// to its light, by the tier spells of the client's data.
    /// </summary>
    [Collection("guild raids")]
    public class GuildRaidDarknessTests : IDisposable
    {
        private const int Madrepeora = 8324, Willorque = 8252, Gigalodon = 8314;
        private readonly string _file;

        public GuildRaidDarknessTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-raiddark-{Guid.NewGuid():N}.db");
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

        /// <summary>The tiers by their admin names: Palier 3, 2, 1 and 0; full light has none.</summary>
        [Fact]
        public void The_tiers_come_from_the_dispatcher_spell()
        {
            Assert.Equal(32742, GuildRaidDarkness.TierFor(3));
            Assert.Equal(32744, GuildRaidDarkness.TierFor(2));
            Assert.Equal(32745, GuildRaidDarkness.TierFor(1));
            Assert.Equal(32746, GuildRaidDarkness.TierFor(0));
            Assert.Equal(0, GuildRaidDarkness.TierFor(4));
        }

        /// <summary>Bound to a floor's light are the monsters whose criterion names it; Willorque and the Gigalodón are not.</summary>
        [Fact]
        public void Only_the_monsters_bound_to_the_light_get_it()
        {
            string common = DatabaseManager.MonsterAggressiveImmunity(Madrepeora);
            for (int floor = 1; floor <= 5; floor++) Assert.True(GuildRaidDarkness.BoundToLight(common, floor));
            Assert.False(GuildRaidDarkness.BoundToLight(common, 6));
            Assert.False(GuildRaidDarkness.BoundToLight(DatabaseManager.MonsterAggressiveImmunity(Willorque), 6));
            Assert.False(GuildRaidDarkness.BoundToLight(DatabaseManager.MonsterAggressiveImmunity(Gigalodon), 1));
            Assert.False(GuildRaidDarkness.BoundToLight("RV!7,n12_worldlight,0", 1));
        }

        /// <summary>
        /// In darkness the darkest tier: +200 % vitality -- the life it has too, it starts full --,
        /// +1000 power and +2 MP, all for good.
        /// </summary>
        [Fact]
        public void The_darkest_tier_triples_a_monster_s_life()
        {
            var fight = new FightInstance(1, 1);
            var monster = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 300, MaxHP = 1000, CurrentHP = 1000, Level = 200, Vitality = 1000,
                MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = Madrepeora,
            };
            fight.AddMonster(monster);

            var outcomes = EffectEngine.Resolver(fight, monster, GuildRaidDarkness.TierFor(0), 1, monster,
                                                 EffectEngine.AlLanzar, 1, celdaApuntada: monster.CellId);

            Assert.Equal(3000, monster.MaxHP);
            Assert.Equal(3000, monster.CurrentHP);
            Assert.Contains(outcomes, o => o.Efecto?.EffectId == 138);
            Assert.Contains(outcomes, o => o.Efecto?.EffectId == 128);
            Assert.All(outcomes.Where(o => o.Buff != null), o => Assert.Equal(-1, o.Buff.CaducaEnRonda));
        }

        /// <summary>A raid fight on floor 2: at light 0 its Madrepeora takes Palier 0, at full light nothing.</summary>
        [Fact]
        public async Task A_raid_fight_takes_its_floor_s_tier()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.SpendGuildKamas(guild.Id, -1000);
            var board = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            var raid = await GuildRaidManager.LaunchAsync(board);
            raid.Add(7001);          // inside, as he would be on coming in connected
            long floor2 = DatabaseManager.MapsOfSubArea(Raids.Of(Raids.Gigalodon).Floors[1]).Min();

            FightInstance FightWith(params int[] monsters)
            {
                var fight = new FightInstance(1, floor2);
                long id = -1;
                foreach (int template in monsters)
                    fight.AddMonster(new Fighter { Id = id--, TeamId = 1, MaxHP = 100, CurrentHP = 100, IsMonster = true, MonsterId = template });
                return fight;
            }

            raid.SetLight(2, 0, DateTimeOffset.UtcNow);
            var dark = FightWith(Madrepeora, Willorque);
            Assert.Equal(1, GuildRaidDarkness.Boost(dark, 7001));
            Assert.Contains(32746, dark.Rojo.First(m => m.MonsterId == Madrepeora).Buffs.Actitudes);
            Assert.Empty(dark.Rojo.First(m => m.MonsterId == Willorque).Buffs.Actitudes);

            // The abyss's monsters start with the dispatcher itself: no attitude, the tier comes through it.
            Assert.True(GuildRaidDarkness.CastsDarkThoughts(GuildRaidDarkness.DarkThoughts, 1));
            var started = FightWith(Madrepeora);
            started.Rojo[0].Conducta = (GuildRaidDarkness.DarkThoughts, 1);
            Assert.Equal(0, GuildRaidDarkness.Boost(started, 7001));
            Assert.Empty(started.Rojo[0].Buffs.Actitudes);
            Assert.Equal(32746, started.DarknessTier);

            raid.SetLight(2, 4, DateTimeOffset.UtcNow);
            var lit = FightWith(Madrepeora);
            Assert.Equal(0, GuildRaidDarkness.Boost(lit, 7001));
            Assert.Empty(lit.Rojo[0].Buffs.Actitudes);

            // Outside any raid, nothing.
            Assert.Equal(0, GuildRaidDarkness.Boost(FightWith(Madrepeora), 9999));

            // And a salt deposit's salt goes into this raid's pool; outside one, nowhere.
            long before = raid.Get(RaidInstance.SaltVariable);
            Assert.True(GuildRaidSaltDeposits.IntoThePool(7001, GuildRaidSaltDeposits.SaltPerDeposit));
            Assert.Equal(before + 1, raid.Get(RaidInstance.SaltVariable));
            Assert.False(GuildRaidSaltDeposits.IntoThePool(9999, 1));
        }
    }
}
