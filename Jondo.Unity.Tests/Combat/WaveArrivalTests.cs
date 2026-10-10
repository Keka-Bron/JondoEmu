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
    /// A wave coming on the way the client draws one arriving, read from its code (no capture has a
    /// wave). The jyb makes a wave the FightBattleService's current one; at the end of the next
    /// sequence that service draws every living fighter that has no entity yet, and to those whose
    /// wave -- f4 of the monster block -- is the current one it gives the light falling on the cell
    /// (gfx 2715). A fighters list (jxb) inside a sequence registers newcomers without drawing them.
    /// </summary>
    [Collection("MapManager")]
    public class WaveArrivalTests
    {
        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        private static long? Var(List<ProtoField> fields, int number)
            => fields.FirstOrDefault(f => f.FieldNumber == number && f.WireType == 0)?.VarIntValue;

        private static List<ProtoField> Inner(List<ProtoField> fields, int number)
            => Fields(fields.First(f => f.FieldNumber == number && f.WireType == 2).BytesValue);

        /// <summary>jyb: f1 the team, f2 the wave, f3 the turns before the next; zeros left out.</summary>
        [Fact]
        public void The_new_wave_message()
        {
            var monsters = Fields(FightProtocol.BuildNewWave(team: 1, wave: 3, turnsBeforeNext: 0));
            Assert.Equal(1, Var(monsters, 1));
            Assert.Equal(3, Var(monsters, 2));
            Assert.Null(Var(monsters, 3));

            var players = Fields(FightProtocol.BuildNewWave(team: 0, wave: 2, turnsBeforeNext: 4));
            Assert.Null(Var(players, 1));
            Assert.Equal(2, Var(players, 2));
            Assert.Equal(4, Var(players, 3));
        }

        /// <summary>
        /// The block's fighter (f2 of f2) carries the wave in f4, which the client copies to the
        /// entity's wave; its place block (f7) says in f3 whether the fighter is alive.
        /// </summary>
        [Fact]
        public void A_monster_block_carries_its_wave_and_whether_it_lives()
        {
            var sheet = new[] { (0, 1000L, 0L) };
            var identity = FightProtocol.MonsterIdentity(1, 494, 50);

            var arrival = Fields(FightProtocol.FighterBlock(300, 1, -7, sheet, null, identity,
                                                            isMonster: true, wave: 2).Build());
            var fighter = Inner(Inner(arrival, 2), 2);
            Assert.Equal(2, Var(fighter, 4));
            Assert.Equal(1, Var(Inner(fighter, 7), 3));

            var fallen = Fields(FightProtocol.FighterBlock(300, 1, -1, sheet, null, identity,
                                                           isMonster: true, alive: false).Build());
            var dead = Inner(Inner(fallen, 2), 2);
            Assert.Null(Var(dead, 4));
            Assert.Null(Var(Inner(dead, 7), 3));
        }

        /// <summary>
        /// A dream's next wave: first the jyb with its number, then the fighters list inside a
        /// sequence of its own -- the newcomers with the wave in their block, the fallen one no longer
        /// alive -- and no summon among them.
        /// </summary>
        [Fact]
        public async Task A_dream_s_next_wave_comes_on_as_an_arrival()
        {
            const long dreamer = 900_000_000_002;
            Interactives.Initialize();
            MobSpawnManager.EnsureMonsterData();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(dreamer, "Prueba", 200, 2, 100, 200);
            for (int band = 2; band <= Dreams.Bands; band++) Dreams.AnadirFranja(dream);
            var end = dream.Salas.Single(r => r.EsFinal);
            dream.Actual = end.Id;

            await using var pipe = await ClientPipe.OpenAsync(990_000_201, dreamer, end.MapaDeLaSala, "Soñador");
            try
            {
                var fight = new FightInstance(7_000_010, end.MapaDeLaSala);
                fight.SetPlacementCells(new[] { 286, 298, 326 }, new[] { 411, 424, 439, 397, 410, 426, 438, 453 });
                fight.AddPlayer(new Fighter
                {
                    Id = dreamer, Name = "Soñador", Level = 200, MaxHP = 5000, CurrentHP = 5000,
                    MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
                });
                var fallen = new Fighter
                {
                    Id = -1, Level = 200, MaxHP = 1000, CurrentHP = 1000, IsMonster = true, MonsterId = 494,
                };
                fight.AddMonster(fallen);

                using (SessionContext.Push(pipe.Session)) DreamHandler.OnFightCreated(fight);
                fallen.CurrentHP = 0;

                bool cameOn;
                using (SessionContext.Push(pipe.Session))
                {
                    cameOn = await FightHandler.NextDreamWaveAsync(pipe.ToClient, fight);
                }
                Assert.True(cameOn);

                // The wave's own monsters: what their behaviour spells summon right after comes on
                // as any summon, with no wave.
                var arrivals = fight.Rojo.Where(f => f.Id != fallen.Id && !f.EsInvocado).ToList();
                Assert.NotEmpty(arrivals);
                Assert.All(arrivals, a => Assert.Equal(2, a.Wave));

                var frames = await pipe.Take(4);
                Assert.Equal(new[] { Op.Jyb, Op.Jto, Op.Jxb, Op.Jwi }, frames.Select(f => f.Opcode).ToArray());

                var wave = Fields(frames[0].Payload);
                Assert.Equal(1, Var(wave, 1));
                Assert.Equal(2, Var(wave, 2));

                var blocks = Fields(frames[2].Payload).Where(f => f.FieldNumber == 1)
                    .Select(f => Fields(f.BytesValue))
                    .ToDictionary(b => Var(b, 3) ?? 0, b => Inner(Inner(b, 2), 2));
                foreach (var arrival in arrivals)
                {
                    Assert.Equal(2, Var(blocks[arrival.Id], 4));
                    Assert.Equal(1, Var(Inner(blocks[arrival.Id], 7), 3));
                }
                Assert.Null(Var(Inner(blocks[fallen.Id], 7), 3));
            }
            finally
            {
                DatabaseManager.DeleteDream(dreamer);
                Dreams.OlvidarTodo();
            }
        }
    }
}
