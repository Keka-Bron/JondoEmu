using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The divine dimensions' dungeons (content/fights/dimension_dungeons.json): their boss room is
    /// fought in waves by the clock, the newcomers invulnerable until their first turn is over and,
    /// after the first wave, with a quarter less life.
    /// </summary>
    [Collection("MapManager")]
    public class DimensionDungeonTests
    {
        /// <summary>The Ring of Captain Eskarlata, Srambad's dimension dungeon: its single room.</summary>
        private const long Ring = 136578048;
        private const int Eskarlata = 3753;

        private static MobSpawnManager.MobMember Member(int monster, int grade = 0)
            => new MobSpawnManager.MobMember { Monster = new MobSpawnManager.MonsterData { Id = monster }, GradeIndex = grade };

        [Fact]
        public void The_rules_are_read_from_the_content()
        {
            DimensionDungeons.Forget();
            var rules = DimensionDungeons.Current;
            Assert.Equal(5, rules.Waves);
            Assert.Equal(5, rules.EveryTurns);
            Assert.Equal(56, rules.ArrivalState);
            Assert.Equal(75, rules.LifePercentFromSecondWave);
            Assert.Contains(81, rules.Dungeons);
        }

        /// <summary>
        /// Four more waves, each the size asked, every kind of the room's monsters in each before
        /// any comes twice, and never the boss.
        /// </summary>
        [Fact]
        public void The_later_waves_are_drawn_from_the_room_s_monsters_without_the_boss()
        {
            DimensionDungeons.Forget();
            var group = new List<MobSpawnManager.MobMember>
            {
                Member(Eskarlata, 2), Member(3730, 1), Member(3731, 3), Member(3732), Member(3730, 1),
            };

            var waves = DimensionDungeons.Plan(group, new[] { Eskarlata }, 4, seed: 7);

            Assert.Equal(4, waves.Count);
            Assert.All(waves, w => Assert.Equal(5, w.Turns));
            Assert.All(waves, w => Assert.Equal(4, w.Monsters.Count));
            Assert.All(waves, w => Assert.DoesNotContain(w.Monsters, m => m.Monster == Eskarlata));
            Assert.All(waves, w => Assert.Equal(new[] { 3730, 3731, 3732 }, w.Monsters.Select(m => m.Monster).Distinct().OrderBy(m => m)));
            Assert.All(waves, w => Assert.All(w.Monsters.Where(m => m.Monster == 3731), m => Assert.Equal(3, m.Grade)));

            // The same fight draws the same waves.
            var again = DimensionDungeons.Plan(group, new[] { Eskarlata }, 4, seed: 7);
            Assert.Equal(waves.SelectMany(w => w.Monsters), again.SelectMany(w => w.Monsters));

            // A group of bosses alone has nothing to send.
            Assert.Empty(DimensionDungeons.Plan(new[] { Member(Eskarlata) }, new[] { Eskarlata }, 4, seed: 7));
        }

        /// <summary>The Ring's room fights in waves; a room of a dungeon not listed does not.</summary>
        [Fact]
        public void The_ring_fights_in_waves()
        {
            DimensionDungeons.Forget();
            DungeonManager.Initialize();
            if (!DungeonManager.IsLoaded || DungeonManager.OfRoom(Ring) == null) return;   // no world base

            Assert.True(DimensionDungeons.FightsInWaves(Ring));
            Assert.False(DimensionDungeons.FightsInWaves(121374209));   // a room of an ordinary dungeon
        }

        /// <summary>
        /// A dimension wave comes on with a quarter less life, its monsters under the invulnerable
        /// state, each row told to the board after the arrivals; the end of a newcomer's first turn
        /// takes it away and tells the board.
        /// </summary>
        [Fact]
        public async Task A_dimension_wave_comes_shielded_and_weaker()
        {
            MobSpawnManager.EnsureMonsterData();
            if (MobSpawnManager.GetMonsterData(3730) == null) return;   // no world base to read

            int full = await ArrivalLifeAsync(7_000_020, 100, 0);
            Assert.True(full > 1);

            const long player = 900_000_000_005;
            await using var pipe = await ClientPipe.OpenAsync(990_000_205, player, Ring, "Retador");
            var fight = Ring_fight(7_000_021, player);
            fight.WaveArrivalState = 56;
            fight.WaveLifePercent = 75;

            using (SessionContext.Push(pipe.Session))
            {
                Assert.True(await FightHandler.NextTimedWaveAsync(pipe.ToClient, fight));
            }

            var arrival = fight.Rojo.Single(f => f.Wave == 1 && !f.EsInvocado);
            Assert.Equal(full * 75 / 100, arrival.MaxHP);
            Assert.Equal(arrival.MaxHP, arrival.CurrentHP);
            Assert.Contains(56, arrival.Buffs.Estados);
            Assert.True(fight.ArrivalShields.ContainsKey(arrival.Id));

            // The wave, the turn order again with the newcomer in it, then the rows in a sequence.
            var frames = await pipe.Take(8);
            Assert.Equal(new[] { Op.Jyb, Op.Jto, Op.Jxb, Op.Jwi, Op.Jzu, Op.Jto, Op.Jxm, Op.Jwi }, frames.Select(f => f.Opcode).ToArray());

            using (SessionContext.Push(pipe.Session))
            {
                await FightHandler.ArrivalShieldEndsAsync(fight, arrival);
            }
            Assert.DoesNotContain(56, arrival.Buffs.Estados);
            Assert.False(fight.ArrivalShields.ContainsKey(arrival.Id));
            var gone = await pipe.Take(3);
            Assert.Equal(new[] { Op.Jto, Op.Jya, Op.Jwi }, gone.Select(f => f.Opcode).ToArray());

            // Only once: the next turn's end has nothing to take away.
            using (SessionContext.Push(pipe.Session))
            {
                await FightHandler.ArrivalShieldEndsAsync(fight, arrival);
            }
        }

        private static FightInstance Ring_fight(int id, long player)
        {
            var fight = new FightInstance(id, Ring);
            fight.SetPlacementCells(new[] { 286, 298, 326 }, new[] { 411, 424, 439, 397, 410, 426, 438, 453 });
            fight.AddPlayer(new Fighter
            {
                Id = player, Name = "Retador", Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            });
            fight.AddMonster(new Fighter { Id = -1, Level = 200, MaxHP = 3000, CurrentHP = 3000, IsMonster = true, MonsterId = Eskarlata });
            fight.TimedWaves.Add(new FightInstance.TimedWave(5, new[] { (3730, 0) }));
            fight.NextWaveRound = 1 + 5;
            return fight;
        }

        /// <summary>The life the wave's 3730 comes with, under the given share and state.</summary>
        private static async Task<int> ArrivalLifeAsync(int id, int lifePercent, int state)
        {
            const long player = 900_000_000_004;
            await using var pipe = await ClientPipe.OpenAsync(990_000_204, player, Ring, "Retador");
            var fight = Ring_fight(id, player);
            fight.WaveArrivalState = state;
            fight.WaveLifePercent = lifePercent;
            using (SessionContext.Push(pipe.Session))
            {
                Assert.True(await FightHandler.NextTimedWaveAsync(pipe.ToClient, fight));
            }
            var arrival = fight.Rojo.Single(f => f.Wave == 1 && !f.EsInvocado);
            Assert.DoesNotContain(56, arrival.Buffs.Estados);
            Assert.Empty(fight.ArrivalShields);
            return arrival.MaxHP;
        }
    }
}
