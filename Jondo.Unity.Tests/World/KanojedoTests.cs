using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The kanojedo against the Hipermago capture and the client's data: the puch
    /// master's conversation, the puchs he can send, the fixed bags and the rule book.
    /// </summary>
    public class KanojedoTests
    {
        /// <summary>
        /// The replies' arithmetic, against the texts of 7416 in world.db: the id we
        /// send for «nivel 50» says level 50, and the one for «tres puchs» says three.
        /// </summary>
        [Fact]
        public void The_masters_replies_say_what_the_client_will_draw()
        {
            if (!File.Exists(Paths.WorldDb)) return;

            var (messages, replies) = MasterTexts();
            Assert.Equal(36, replies.Count);

            var levels = Kanojedo.LevelReplies;
            Assert.Equal(6, levels.Count);
            int[] expected = { 200, 100, 75, 50, 25, 1 };
            for (int i = 0; i < 6; i++)
            {
                Assert.Equal(expected[i], Kanojedo.LevelAt(i));
                Assert.Equal(i, Kanojedo.LevelIndexOf(levels[i]));
                Assert.Contains("nivel " + expected[i] + ".", replies[levels[i]]);

                // The second screen: one, two, three, four and back.
                var counts = Kanojedo.CountReplies(i);
                Assert.Equal(5, counts.Count);
                Assert.StartsWith("Entrenarte con 1 puch", replies[counts[0]]);
                Assert.StartsWith("Entrenarte con 2 puchs", replies[counts[1]]);
                Assert.StartsWith("Entrenarte con 3 puchs", replies[counts[2]]);
                Assert.StartsWith("Entrenarte con 4 puchs", replies[counts[3]]);
                Assert.StartsWith("Ir a otro nivel", replies[counts[4]]);

                for (int n = 1; n <= 4; n++)
                {
                    var read = Kanojedo.ReadCount(counts[n - 1]);
                    Assert.NotNull(read);
                    Assert.Equal((i, n), read!.Value);
                }

                Assert.True(Kanojedo.IsBack(counts[4]));
                Assert.Null(Kanojedo.ReadCount(counts[4]));

                // And all the sentences are the ellipsis the master says.
                Assert.Equal("...", messages[Kanojedo.MessageFor(i)]);
            }

            Assert.Equal("...", messages[Kanojedo.FirstMessage]);

            // The two branches the capture really walks.
            Assert.Equal(54969, Kanojedo.MessageFor(Kanojedo.LevelIndexOf(73825)));   // nivel 50
            Assert.Equal(new long[] { 73820, 73821, 73822, 73823, 73824 },
                         Kanojedo.CountReplies(Kanojedo.LevelIndexOf(73825)));
            Assert.Equal(54970, Kanojedo.MessageFor(Kanojedo.LevelIndexOf(73831)));   // nivel 25
            Assert.Equal(new long[] { 73826, 73827, 73828, 73829, 73830 },
                         Kanojedo.CountReplies(Kanojedo.LevelIndexOf(73831)));

            Assert.False(Kanojedo.Owns(7846));
        }

        /// <summary>
        /// The puchs the master can send come from the base: breed 250 and a name the
        /// client knows how to draw. Six, and at level 200 only the Ingball.
        /// </summary>
        [Fact]
        public void The_puchs_come_out_of_the_database()
        {
            if (!File.Exists(Paths.WorldDb)) return;

            Kanojedo.Forget();
            var puchs = Kanojedo.Puchs;
            Assert.Equal(6, puchs.Count);
            Assert.Equal(new[] { 494, 3588, 3589, 3590, 3591, 3592 }, puchs.Select(p => p.MonsterId).OrderBy(x => x));
            Assert.DoesNotContain(puchs, p => p.Name.StartsWith("[!]"));
            Assert.Contains(puchs, p => p.Name == "Puch Cráneo Rosa");

            // At 50 all six, at grade 2; at 200 only the Ingball, at grade 5, the sixth.
            var at50 = Kanojedo.PoolAt(50);
            Assert.Equal(6, at50.Count);
            Assert.All(at50, p => Assert.Equal(2, p.Grade));

            var at200 = Kanojedo.PoolAt(200);
            Assert.Single(at200);
            Assert.Equal((494, 5), at200[0]);

            Assert.Empty(Kanojedo.PoolAt(42));

            // Choosing: as many as asked for, all from the pool, and with repetition when there are no more.
            var dice = new Random(7);
            var four = Kanojedo.Pick(50, 4, dice);
            Assert.Equal(4, four.Count);
            Assert.All(four, p => Assert.Contains(p, at50));

            Assert.Equal(new[] { (494, 5), (494, 5), (494, 5), (494, 5) }, Kanojedo.Pick(200, 4, dice));
            Assert.Single(Kanojedo.Pick(1, 1, dice));
            Assert.Equal(4, Kanojedo.Pick(25, 9, dice).Count);

            // And with the repeatable die, two sessions of four do not come out the same: they are themed.
            var many = new HashSet<int>();
            for (int i = 0; i < 40; i++)
            {
                foreach (var (monster, _) in Kanojedo.Pick(75, 4, dice)) many.Add(monster);
            }
            Assert.True(many.Count >= 4);
        }

        /// <summary>
        /// The six bags of each kanojedo, just as the capture puts them: cell, orientation and
        /// grade, with the level 200 one at the sixth grade.
        /// </summary>
        [Fact]
        public void The_punching_bags_stand_where_the_capture_put_them()
        {
            var groups = MobGroupContent.Load(Paths.ContentFile(MobGroupContent.AuthoredFile));
            var amakna = groups.Values.Where(g => g.MapId == 99090957).OrderBy(g => g.Cell).ToList();
            Assert.Equal(6, amakna.Count);

            var expected = new (int Cell, int Facing, int Grade)[]
            {
                (285, 3, 3), (288, 1, 4), (325, 3, 2), (331, 3, 5), (409, 1, 1), (453, 5, 0),
            };
            for (int i = 0; i < 6; i++)
            {
                Assert.Equal(expected[i].Cell, amakna[i].Cell);
                Assert.Equal(expected[i].Facing, amakna[i].Orientation);
                Assert.Single(amakna[i].Members);
                Assert.Equal(494, amakna[i].Members[0].MonsterId);
                Assert.Equal(expected[i].Grade, amakna[i].Members[0].Grade);
            }

            // One per grade, none repeated.
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, amakna.Select(g => g.Members[0].Grade).OrderBy(x => x));

            // And the testers' dojo, from the Cra capture.
            var tester = groups.Values.Where(g => g.MapId == 146801922).ToList();
            Assert.Equal(6, tester.Count);
            Assert.Contains(tester, g => g.Cell == 456 && g.Members[0].Grade == 5);
        }

        /// <summary>The training book: against monsters, but with nothing at stake.</summary>
        [Fact]
        public void Training_is_a_monster_fight_with_nothing_at_stake()
        {
            var rules = FightRules.Entrenamiento;
            Assert.Equal(4, rules.TipoDelKam);
            Assert.True(rules.EnfrenteHayMonstruos);
            Assert.True(rules.KaaConCuentaAtras);
            Assert.Equal(FightRules.ContraMonstruos.RelojDeColocacion, rules.RelojDeColocacion);

            Assert.False(rules.HayRetos);
            Assert.False(rules.ReparteBotin);
            Assert.False(rules.PagaElKoliseo);
            Assert.False(rules.BorraElGrupoAlGanar);
            Assert.False(rules.AvanzaDeSala);
        }

        /// <summary>The measured arenas: four kanojedos, each with its own.</summary>
        [Fact]
        public void The_measured_arenas_are_the_captures()
        {
            MeasuredArenas.Forget();
            Assert.Equal(4, MeasuredArenas.Count);
            Assert.Equal(99222029, MeasuredArenas.Of(99090957));
            Assert.Equal(146803972, MeasuredArenas.Of(146801922));
            Assert.Equal(146804996, MeasuredArenas.Of(146802688));
            Assert.Equal(192419850, MeasuredArenas.Of(192415754));
            Assert.Equal(0, MeasuredArenas.Of(191105026));
        }

        /// <summary>
        /// The door: from 88212247 to the kanojedo, through element 472901 on cell 315, which
        /// is the one the map's jss declares with skill 184 and the map puts next to the
        /// guardian.
        /// </summary>
        [Fact]
        public void The_door_into_the_kanojedo_is_written()
        {
            var passages = TeleportContent.Load(Paths.ContentFile(TeleportContent.AuthoredFile));
            var door = passages.Values.Single(p => p.SourceMapId == 88212247 && p.ElementId == 472901);
            Assert.Equal(315, door.SourceCell);
            Assert.Equal(184, door.SkillId);
            Assert.Equal(99090957, door.DestinationMapId);
            Assert.Equal(457, door.DestinationCell);
        }

        private static (Dictionary<long, string> Messages, Dictionary<long, string> Replies) MasterTexts()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();

            var template = connection.CreateCommand();
            template.CommandText = "SELECT Data FROM NpcTemplates WHERE Id = $id;";
            template.Parameters.AddWithValue("$id", Kanojedo.MasterNpc);
            using var doc = JsonDocument.Parse((string)template.ExecuteScalar()!);

            string Text(long key)
            {
                var read = connection.CreateCommand();
                read.CommandText = "SELECT Text FROM Translations WHERE Key = $k;";
                read.Parameters.AddWithValue("$k", key.ToString());
                return read.ExecuteScalar() as string ?? "";
            }

            Dictionary<long, string> Pairs(string field)
            {
                var found = new Dictionary<long, string>();
                foreach (var entry in doc.RootElement.GetProperty(field).GetProperty("Array").EnumerateArray())
                {
                    var values = entry.GetProperty("values").GetProperty("Array");
                    found[values[0].GetInt64()] = Text(values[1].GetInt64());
                }

                return found;
            }

            return (Pairs("dialogMessages"), Pairs("dialogReplies"));
        }
    }

    /// <summary>
    /// What touches MapManager's static state: the measured arena beats the rule.
    /// </summary>
    [Collection("MapManager")]
    public class KanojedoArenaTests
    {
        [Fact]
        public void The_measured_arena_wins_over_the_rule()
        {
            var antes = MapManager.Maps;
            try
            {
                MapManager.Maps = new Dictionary<long, MapInfo>
                {
                    [99090957] = new MapInfo { MapId = 99090957, SubAreaId = 10, Outdoor = false, Name = "The Kanojedo", Flags = 72467485 },
                    [99222029] = new MapInfo { MapId = 99222029, SubAreaId = 10, Outdoor = true, Name = "", Flags = 69262589 },
                    [99098121] = new MapInfo { MapId = 99098121, SubAreaId = 10, Outdoor = true, Name = "", Flags = 69262589 },
                };

                Assert.Equal(99222029, MapManager.ResolveArenaMapId(99090957));

                // Without the measured arena in the world, the usual rule.
                MapManager.Maps.Remove(99222029);
                Assert.NotEqual(99222029, MapManager.ResolveArenaMapId(99090957));
            }
            finally
            {
                MapManager.Maps = antes;
            }
        }
    }
}
