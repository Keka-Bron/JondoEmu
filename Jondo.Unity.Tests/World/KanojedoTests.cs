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
    /// El kanojedo contra la captura del Hipermago y los datos del cliente: la conversación del
    /// puch maestro, los puchs que puede mandar, los sacos fijos y el libro de reglas.
    /// </summary>
    public class KanojedoTests
    {
        /// <summary>
        /// La aritmética de las respuestas, contra los textos del 7416 en world.db: el id que
        /// mandamos para «nivel 50» dice nivel 50, y el de «tres puchs» dice tres.
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

                // La segunda pantalla: una, dos, tres, cuatro y volver.
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

                // Y todas las frases son las tres puntos que el maestro dice.
                Assert.Equal("...", messages[Kanojedo.MessageFor(i)]);
            }

            Assert.Equal("...", messages[Kanojedo.FirstMessage]);

            // Los dos ramales que la captura recorre de verdad.
            Assert.Equal(54969, Kanojedo.MessageFor(Kanojedo.LevelIndexOf(73825)));   // nivel 50
            Assert.Equal(new long[] { 73820, 73821, 73822, 73823, 73824 },
                         Kanojedo.CountReplies(Kanojedo.LevelIndexOf(73825)));
            Assert.Equal(54970, Kanojedo.MessageFor(Kanojedo.LevelIndexOf(73831)));   // nivel 25
            Assert.Equal(new long[] { 73826, 73827, 73828, 73829, 73830 },
                         Kanojedo.CountReplies(Kanojedo.LevelIndexOf(73831)));

            Assert.False(Kanojedo.Owns(7846));
        }

        /// <summary>
        /// Los puchs que el maestro puede mandar salen de la base: raza 250 y un nombre que el
        /// cliente sepa pintar. Seis, y al nivel 200 sólo el Ingball.
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

            // Al 50 los seis, a grado 2; al 200 sólo el Ingball, a grado 5, el sexto.
            var at50 = Kanojedo.PoolAt(50);
            Assert.Equal(6, at50.Count);
            Assert.All(at50, p => Assert.Equal(2, p.Grade));

            var at200 = Kanojedo.PoolAt(200);
            Assert.Single(at200);
            Assert.Equal((494, 5), at200[0]);

            Assert.Empty(Kanojedo.PoolAt(42));

            // Elegir: tantos como se pidan, todos del pozo, y con repetición cuando no hay más.
            var dice = new Random(7);
            var four = Kanojedo.Pick(50, 4, dice);
            Assert.Equal(4, four.Count);
            Assert.All(four, p => Assert.Contains(p, at50));

            Assert.Equal(new[] { (494, 5), (494, 5), (494, 5), (494, 5) }, Kanojedo.Pick(200, 4, dice));
            Assert.Single(Kanojedo.Pick(1, 1, dice));
            Assert.Equal(4, Kanojedo.Pick(25, 9, dice).Count);

            // Y con el dado repetible, dos sesiones de a cuatro no salen iguales: son temáticos.
            var many = new HashSet<int>();
            for (int i = 0; i < 40; i++)
            {
                foreach (var (monster, _) in Kanojedo.Pick(75, 4, dice)) many.Add(monster);
            }
            Assert.True(many.Count >= 4);
        }

        /// <summary>
        /// Los seis sacos de cada kanojedo, tal cual los pone la captura: casilla, orientación y
        /// grado, con el de nivel 200 al sexto grado.
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

            // Una por grado, ninguna repetida.
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, amakna.Select(g => g.Members[0].Grade).OrderBy(x => x));

            // Y el dojo de los testeadores, de la captura del Ocra.
            var tester = groups.Values.Where(g => g.MapId == 146801922).ToList();
            Assert.Equal(6, tester.Count);
            Assert.Contains(tester, g => g.Cell == 456 && g.Members[0].Grade == 5);
        }

        /// <summary>El libro del entrenamiento: contra monstruos, pero sin nada en juego.</summary>
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

        /// <summary>Las arenas medidas: cuatro kanojedos, cada uno con la suya.</summary>
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
        /// La puerta: del 88212247 al kanojedo, por el elemento 472901 de la casilla 315, que
        /// es el que el jss del mapa declara con la habilidad 184 y el mapa pone al lado del
        /// guardián.
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
    /// Lo que toca el estado estático de MapManager: la arena medida gana a la regla.
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

                // Sin el arena medida en el mundo, la regla de siempre.
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
