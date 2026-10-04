using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// What a dream pays: the loot bonus of its rooms, the reflections of every fight, the dream's
    /// loot table, the rewards' storms and levels, and the dream fragments of the Fin du rêve.
    /// </summary>
    /// <remarks>
    /// Against the captures: the jyg of the invitation capture (frame 5572), 17 reflections to each
    /// of four players at an f8 of 168; the izo of five captures, the reflections line x5, x16,
    /// x17 and x19 at 50, 160, 168 and 190; and against the client's InfiniteDream DataRoots.
    /// </remarks>
    [Collection("MapManager")]
    public class DreamRewardTests
    {
        private const long Dreamer = 900_000_000_002;

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        private static int? Var(List<ProtoField> fields, int number)
            => (int?)fields.FirstOrDefault(f => f.FieldNumber == number && f.WireType == 0)?.VarIntValue;

        /// <summary>
        /// The f8 of the captures where it is not the difficulty's own, and the guide's 238 %:
        /// a tenth more a palier, a twentieth more in a marked room.
        /// </summary>
        [Theory]
        [InlineData(4, 23, false, 168)]    // Paradoja I, invitation capture, rooms 58 and 60
        [InlineData(4, 24, false, 168)]
        [InlineData(1, 5, false, 55)]      // Sueño I, long capture, rooms 12 and 14
        [InlineData(1, 6, false, 55)]
        [InlineData(10, 1, true, 315)]     // Pesadilla III, a marked room of row 1
        [InlineData(8, 1, false, 220)]     // Pesadilla I, room 1: the difficulty's own
        [InlineData(7, 1, false, 190)]
        [InlineData(7, 12, true, 238)]     // the guide's example, 238 % and 24 reflections
        public void The_loot_bonus_is_the_room_s(int difficulty, int row, bool marked, int bonus)
            => Assert.Equal(bonus, Dreams.LootBonusOf(difficulty, row, marked));

        /// <summary>Ten times the bonus, rounded up: the izo's x5, x16, x17, x19 and the guide's 24.</summary>
        [Theory]
        [InlineData(50, 5)]
        [InlineData(160, 16)]
        [InlineData(168, 17)]
        [InlineData(190, 19)]
        [InlineData(238, 24)]
        public void Reflections_are_ten_times_the_bonus_rounded_up(int bonus, int reflections)
            => Assert.Equal(reflections, Dreams.ReflectionsFor(bonus));

        /// <summary>
        /// Entering rooms moves the f8 as the long capture does: 50 through band I, 50 still at the
        /// fountain of row 4, 55 from row 5 on.
        /// </summary>
        [Fact]
        public void Entering_rooms_moves_the_bonus()
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(1, "Prueba", 200, 1, 100, 200);
            Dreams.Enter(dream, 0, out _);

            var first = dream.Salas.First(r => r.Fila == 1);
            Dreams.Enter(dream, first.Id, out _);
            Assert.Equal(50, dream.Bonus);

            var fountain = dream.Salas.Single(r => r.EsFuente);
            Dreams.Enter(dream, fountain.Id, out _);
            Dreams.AnadirFranja(dream);
            Assert.Equal(50, dream.Bonus);

            var row5 = dream.Salas.First(r => r.Fila == 5 && r.Miembros.Count > 0 && !r.Senalada);
            Dreams.Enter(dream, row5.Id, out _);
            Assert.Equal(55, dream.Bonus);
            Assert.Equal(55, Var(Fields(DreamProtocol.BuildDreamState(dream)), 8));
            Assert.Equal(50, Var(Fields(DreamProtocol.BuildDreamState(dream)), 22));
        }

        /// <summary>
        /// A dream fight's winner takes the reflections whole whatever the dice say: 17 at the
        /// invitation capture's 168, and nothing else when nothing else is rolled.
        /// </summary>
        [Fact]
        public void A_fight_pays_its_reflections_whole()
        {
            var loot = Dreams.LootOf(4, 23, 168, () => 99.9999);
            Assert.Equal(new Dictionary<int, int> { [Dreams.ReflectionItem] = 17 }, loot);
        }

        /// <summary>
        /// The astral runes of a palier and only from Paradoja I: the "Wi&gt;3" of every rune line,
        /// the client's droplegend 0 for the Rêve intensities and the guide's "no astral runes".
        /// In palier V a Paradoja drops the legendary and the marvellous ones, the invitation
        /// capture's 21968 among them.
        /// </summary>
        [Fact]
        public void Astral_runes_by_palier_and_only_from_paradoja()
        {
            int[] runes = { 21964, 21965, 21966, 21967, 21968, 21969 };

            var reve = Dreams.LootOf(3, 23, 140, () => 0);
            Assert.DoesNotContain(reve.Keys, runes.Contains);

            var paradoja = Dreams.LootOf(4, 23, 168, () => 0);
            Assert.Equal(new[] { 21968, 21969 }, paradoja.Keys.Where(runes.Contains).OrderBy(i => i));

            var palierOne = Dreams.LootOf(4, 1, 120, () => 0);
            Assert.Equal(new[] { 21964, 21965 }, palierOne.Keys.Where(runes.Contains).OrderBy(i => i));

            Assert.False(DreamData.IntensityOf(3)!.DropsLegends);
            Assert.True(DreamData.IntensityOf(4)!.DropsLegends);
        }

        /// <summary>A line whose criterion asks for a quest never drops: nothing answers it in a dream.</summary>
        [Fact]
        public void Quest_lines_do_not_drop()
        {
            var loot = Dreams.LootOf(10, 23, 300, () => 0);
            foreach (int quest in new[] { 22436, 25907, 30054, 30055, 30056, 30057, 30058, 21190, 21191, 21192, 32092 })
                Assert.DoesNotContain(quest, loot.Keys);
            Assert.Contains(22161, loot.Keys);   // Sueñoscudo estrella, no criterion at all
        }

        /// <summary>
        /// The loot table as the client's window gets it at a bonus of 190, against the izo of
        /// "continuar sueño infinito": 61 lines, x19 reflections, the minor rune of palier 1 at
        /// 7.6 %, the legendary of palier 4 at 1.273 %, a legend at 0.007 %, a report at 100 %.
        /// </summary>
        [Fact]
        public void The_loot_table_is_the_capture_s()
        {
            var table = Dreams.LootTableAt(190);
            Assert.Equal(61, table.Count);

            (string, int, int, double) Line(int item, string criterion) => table.Single(l => l.Item == item && l.Criterion == criterion);
            Assert.Equal(19, table.Single(l => l.Item == Dreams.ReflectionItem).Quantity);
            Assert.Equal(7.6, Line(21964, "(Wp=1&Wi>3)").Item4, 3);
            Assert.Equal(1.273, Line(21968, "(Wp=4&Wi>3)").Item4, 3);
            Assert.Equal(0.007, Line(20658, "(Wi>3&Wp>2)").Item4, 3);
            Assert.Equal(100.0, Line(30054, "(Sc=940&(Qa=2498|Qf=2498)&Wp=1)").Item4, 3);

            // And at the Sueño I capture's 50: x5, the same rune at 2 %.
            var low = Dreams.LootTableAt(50);
            Assert.Equal(5, low.Single(l => l.Item == Dreams.ReflectionItem).Quantity);
            Assert.Equal(2.0, low.Single(l => l.Item == 21964 && l.Criterion == "(Wp=1&Wi>3)").Percent, 3);
        }

        /// <summary>
        /// The client's intensities agree with the tables measured in the captures: money is the
        /// dream points a dream starts with, additionalLife its sand, dropBonus its f22.
        /// </summary>
        [Fact]
        public void The_client_s_intensities_are_the_measured_ones()
        {
            for (int difficulty = 1; difficulty <= Dreams.MaximaDificultad; difficulty++)
            {
                var intensity = DreamData.IntensityOf(difficulty);
                Assert.NotNull(intensity);
                Assert.Equal(Dreams.StartingDreamPoints(difficulty), intensity!.Money);
                Assert.Equal(Dreams.StartingArenas(difficulty), intensity.AdditionalLife);
                Assert.Equal(Dreams.BonusOf(difficulty), (int)Math.Round(intensity.DropBonus * 100));
            }
        }

        /// <summary>
        /// The dream fragments of the Fin du rêve: 25 a wave in Rêve I up to 1000 in Cauchemar III,
        /// the guide's table and the client's dreamFragments.
        /// </summary>
        [Theory]
        [InlineData(1, 5, 125)]
        [InlineData(2, 5, 250)]
        [InlineData(4, 3, 225)]
        [InlineData(7, 15, 3000)]
        [InlineData(10, 7, 7000)]
        [InlineData(5, 0, 0)]
        public void The_end_pays_its_waves_in_dream_fragments(int difficulty, int waves, int fragments)
            => Assert.Equal(fragments, Dreams.FragmentsFor(difficulty, waves));

        /// <summary>
        /// What a room's reward adds matches the client's reward rows: a dream-point action a point,
        /// a storm action a storm -- 118, "Tormenta astral", is one and five.
        /// </summary>
        [Fact]
        public void The_rooms_rewards_are_the_client_s_rows()
        {
            foreach (var reward in Dreams.RoomRewards)
            {
                var row = DreamData.RewardOf(reward.Tag);
                Assert.NotNull(row);
                Assert.Equal(reward.Points, row!.Actions.Count(a => a == DreamData.DreamPointAction));
                Assert.Equal(reward.Storms, row.Actions.Count(a => a == DreamData.StormAction));
                Assert.Equal(reward.Bonuses.Count, row.Actions.Count(a => DreamData.EffectOfAction(a) != 0));
            }
        }

        /// <summary>
        /// Entering a room of reward 118 adds a storm and five dream points -- f7 from 1 to 2 in
        /// three captures -- and one of reward 154 fifty dreamer levels, the izg's f14.
        /// </summary>
        [Fact]
        public void A_room_gives_its_storm_and_its_levels()
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(1, "Prueba", 200, 5, 100, 200);
            Dreams.Enter(dream, 0, out _);
            var rooms = dream.Salas.Where(r => r.Fila == 1).ToList();

            rooms[0].Reward = Dreams.RoomRewards.Single(r => r.Tag == 118);
            int storms = dream.Tormentas, points = dream.DreamPoints;
            Dreams.Enter(dream, rooms[0].Id, out _);
            Assert.Equal(storms + 1, dream.Tormentas);
            Assert.Equal(points + rooms[0].DreamPoints + 5, dream.DreamPoints);

            rooms[1].Reward = Dreams.RoomRewards.Single(r => r.Tag == 154);
            Dreams.Enter(dream, rooms[1].Id, out _);
            Assert.Equal(50, dream.DreamerLevels);
            Assert.Equal(50, Var(Fields(DreamProtocol.BuildDreamState(dream)), 14));
        }

        /// <summary>
        /// A Paradoja's Fin du rêve is won from its third wave even when the dreamer falls to the
        /// fourth: the pay counts it finished, three waves, 300 dream fragments -- and the notice
        /// says so.
        /// </summary>
        [Fact]
        public void The_end_pays_even_to_the_fallen_once_its_waves_are_down()
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = Dreamer;
            try
            {
                using (SessionContext.Push(session))
                {
                    Interactives.Initialize();
                    Dreams.OlvidarTodo();
                    var dream = Dreams.Crear(Dreamer, "Prueba", 200, 5, 100, 200);
                    for (int band = 2; band <= Dreams.Bands; band++) Dreams.AnadirFranja(dream);
                    var end = dream.Salas.Single(r => r.EsFinal);
                    Dreams.Enter(dream, end.Id, out _);

                    var fight = new FightInstance(7_000_101, end.MapaDeLaSala);
                    fight.AddPlayer(new Fighter { Id = Dreamer, Level = 200, MaxHP = 1000, CurrentHP = 0 });
                    fight.AddMonster(new Fighter { Id = -1, Level = 275, MaxHP = 1000, CurrentHP = 1000, IsMonster = true });
                    DreamHandler.OnFightCreated(fight);

                    var early = DreamHandler.PayOf(fight, won: false)!;
                    Assert.False(early.Finished);
                    Assert.Equal(0, early.Fragments);
                    Assert.Equal(26, early.Row);
                    Assert.Equal(Dreams.LootBonusOf(5, 26, true), early.LootBonus);

                    for (int wave = 0; wave < 3; wave++) DreamHandler.NextWave(fight);
                    var pay = DreamHandler.PayOf(fight, won: false)!;
                    Assert.True(pay.Finished);
                    Assert.Equal(3, pay.Waves);
                    Assert.Equal(300, pay.Fragments);

                    var (_, _, notice, ended) = DreamHandler.AfterTheFight(fight, won: false);
                    Assert.True(ended);
                    Assert.Contains("3 oleada", notice);
                    Assert.Contains("300 retazo", notice);
                }
            }
            finally
            {
                DatabaseManager.DeleteDream(Dreamer);
            }
        }

        /// <summary>A fight that is not a dream's is paid as ever: no dream pay.</summary>
        [Fact]
        public void A_world_fight_is_not_a_dream_s()
            => Assert.Null(DreamHandler.PayOf(new FightInstance(7_000_102, 191106048), won: true));
    }
}
