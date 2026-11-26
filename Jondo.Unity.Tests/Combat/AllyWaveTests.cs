using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
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
    /// Allies that come in with a wave, not at the start: the two Refuerzo steamers (5150) of
    /// «Mas alla de la costa» (1943) join the players' side when the third wave arrives. An ally's
    /// «wave» counts the way the guides count them: 1 the group the fight starts with, 2 the first
    /// timed wave, 3 the second.
    /// </summary>
    [Collection("MapManager")]
    public class AllyWaveTests
    {
        private const int Steamer = 5150;
        private const int Horde = 186515970;

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        private static long? Var(List<ProtoField> fields, int number)
            => fields.FirstOrDefault(f => f.FieldNumber == number && f.WireType == 0)?.VarIntValue;

        private static List<ProtoField> Inner(List<ProtoField> fields, int number)
            => Fields(fields.First(f => f.FieldNumber == number && f.WireType == 2).BytesValue);

        /// <summary>
        /// The first of the four rows of the horde fight, read as the server reads it; not through
        /// <see cref="NpcFights.For"/>, whose quest criterion wants a character on that step.
        /// </summary>
        private static NpcFights.Fight? Row()
        {
            string path = Paths.ContentFile(NpcFights.AuthoredFile);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var row in doc.RootElement.GetProperty("fights").EnumerateArray())
            {
                if (row.TryGetProperty("npc", out var npc) && npc.GetInt32() == 4222 &&
                    row.TryGetProperty("map", out var map) && map.GetInt64() == Horde)
                    return NpcFights.Parse(row);
            }
            return null;
        }

        /// <summary>The row: nobody beside the players at the start, both steamers on the second timed wave.</summary>
        [Fact]
        public void The_steamers_of_1943_wait_for_the_third_wave()
        {
            var horde = Row();
            Assert.NotNull(horde);
            Assert.Empty(horde!.Allies);
            Assert.Empty(horde.AllyCells);
            Assert.Equal(2, horde.Waves.Count);
            Assert.Empty(horde.Waves[0].Allies);
            Assert.Equal(new[] { Steamer, Steamer }, horde.Waves[1].Allies.Select(a => a.Monster).ToArray());
            Assert.Equal(8, horde.Waves[1].Monsters.Count);
        }

        /// <summary>A wave the row does not have, or wave 1, is the start.</summary>
        [Theory]
        [InlineData("{\"monster\": 5150, \"grade\": 0}", 1)]
        [InlineData("{\"monster\": 5150, \"grade\": 0, \"wave\": 1}", 1)]
        [InlineData("{\"monster\": 5150, \"grade\": 0, \"wave\": 3}", 3)]
        [InlineData("{\"monster\": 5150, \"grade\": 0, \"wave\": 4}", 1)]
        public void An_ally_s_wave(string ally, int wave)
        {
            using var doc = JsonDocument.Parse(ally);
            Assert.Equal(wave, NpcFights.WaveOf(2, doc.RootElement));
        }

        /// <summary>
        /// The wave with the steamers: they come in on the players' side, on the players' cells,
        /// marked with the wave, in the turns from there, and the client is told as for any wave:
        /// jyb, then the fighters list inside a sequence of its own.
        /// </summary>
        [Fact]
        public async Task The_steamers_come_in_with_their_wave()
        {
            var horde = Row();
            Assert.NotNull(horde);

            const long player = 900_000_000_004;
            MobSpawnManager.EnsureMonsterData();
            if (MobSpawnManager.GetMonsterData(Steamer) == null) return;   // no world base to read

            await using var pipe = await ClientPipe.OpenAsync(990_000_203, player, Horde, "Retador");
            var fight = new FightInstance(7_000_012, Horde);
            var blue = new[] { 286, 298, 326 };
            fight.SetPlacementCells(blue, new[] { 411, 424, 439, 397, 410, 426, 438, 453 });
            fight.AddPlayer(new Fighter
            {
                Id = player, Name = "Retador", Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, CellId = 286,
            });
            fight.AddMonster(new Fighter { Id = -1, Level = 200, MaxHP = 6000, CurrentHP = 6000, IsMonster = true, MonsterId = 5112, CellId = 411 });
            fight.WaveNumber = 1;
            fight.TimedWaves.Add(horde!.Waves[1]);
            fight.NextWaveRound = fight.RoundNumber;

            bool cameOn;
            using (SessionContext.Push(pipe.Session))
            {
                cameOn = await FightHandler.NextTimedWaveAsync(pipe.ToClient, fight);
            }
            Assert.True(cameOn);
            Assert.Equal(2, fight.WaveNumber);

            var steamers = fight.Azul.Where(f => f.IsMonster && f.MonsterId == Steamer).ToList();
            Assert.Equal(2, steamers.Count);
            Assert.All(steamers, s =>
            {
                Assert.Equal(0, s.TeamId);
                Assert.Equal(2, s.Wave);
                Assert.True(s.IsAlive);
                Assert.Contains(s.CellId, blue);
                Assert.Contains(s, fight.TurnOrder);
            });
            Assert.DoesNotContain(fight.Rojo, f => f.MonsterId == Steamer);
            Assert.Equal(8, fight.Rojo.Count(f => f.Wave == 2 && !f.EsInvocado));

            var frames = await pipe.Take(4);
            Assert.Equal(new[] { Op.Jyb, Op.Jto, Op.Jxb, Op.Jwi }, frames.Select(f => f.Opcode).ToArray());
            var blocks = Fields(frames[2].Payload).Where(f => f.FieldNumber == 1)
                .Select(f => Fields(f.BytesValue))
                .ToDictionary(b => Var(b, 3) ?? 0, b => Inner(Inner(b, 2), 2));
            foreach (var steamer in steamers)
            {
                Assert.Equal(2, Var(blocks[steamer.Id], 4));
                Assert.Equal(1, Var(Inner(blocks[steamer.Id], 7), 3));
            }
        }
    }
}
