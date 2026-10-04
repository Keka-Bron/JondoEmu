using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// El cofre de la raid contra los datos del cliente: sus dos pantallas, sus cinco aspectos por
    /// puntuación, y lo que vale cada tesoro, que sale del objeto y no de ninguna lista nuestra.
    /// </summary>
    public class RaidChestTests
    {
        /// <summary>
        /// Los once tesoros del juego y lo que puntúan. No hay lista escrita en ninguna parte: un
        /// tesoro es lo que lleva el efecto 4063, «Valor de un objeto», y lo vale.
        /// </summary>
        [Fact]
        public void A_treasure_is_whatever_carries_a_value()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            RaidTreasures.Forget();
            var values = RaidTreasures.Values;
            Assert.Equal(11, values.Count);

            // Las siete gemas, de la más común a la más rara: cuanto menos cae, más vale.
            Assert.Equal(2, RaidTreasures.ValueOf(32465));    // Quartz
            Assert.Equal(4, RaidTreasures.ValueOf(32466));    // Ópalo
            Assert.Equal(6, RaidTreasures.ValueOf(32467));    // Amazonita
            Assert.Equal(10, RaidTreasures.ValueOf(32468));   // Aventurina
            Assert.Equal(15, RaidTreasures.ValueOf(32469));   // Lapyz
            Assert.Equal(20, RaidTreasures.ValueOf(32471));   // Azabache
            Assert.Equal(30, RaidTreasures.ValueOf(32470));   // Ónix

            // Los tres trofeos de los guardianes, uno por planta y cada vez más hondo.
            Assert.Equal(1000, RaidTreasures.ValueOf(34334));    // Unidad de Morreina
            Assert.Equal(5000, RaidTreasures.ValueOf(34335));    // Rencor de Cangrancio
            Assert.Equal(10000, RaidTreasures.ValueOf(34336));   // Tenebrosidad de Willorca

            // Y la sal, que vale uno: el mismo puñado enciende una franja o puntúa.
            Assert.Equal(1, RaidTreasures.ValueOf(Luminomachine.SaltItem));

            // Lo que no es un tesoro no vale nada, y eso incluye la Pinza de Cangrancio, que sale
            // del mismo monstruo pero es un objeto de verdad y no un tesoro.
            Assert.Equal(0, RaidTreasures.ValueOf(34394));
            Assert.Equal(0, RaidTreasures.ValueOf(44));

            Assert.Equal(1000L * 3 + 30 * 2,
                         RaidTreasures.Worth(new Dictionary<int, int> { [34334] = 3, [32470] = 2 }));
        }

        /// <summary>
        /// Las dos pantallas del cofre, con las frases que el cliente les va a poner: la que avisa
        /// de que tomarlo acaba la raid para todos es la razón de que haya dos.
        /// </summary>
        [Fact]
        public void The_chest_says_what_the_client_will_draw()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            var (messages, replies) = ChestTexts();

            Assert.Equal("*emite una extraña vibración*", messages[RaidChest.Vibrating]);
            Assert.Contains("la raid se acabará para todo el equipo", messages[RaidChest.Warning]);

            Assert.Equal("Soltar todos los tesoros.", replies[RaidChest.DropTreasures]);
            Assert.Contains("Acercarte al cofre", replies[RaidChest.Approach]);
            Assert.Equal("Tomar el cofre y escapar.", replies[RaidChest.TakeAndRun]);
            Assert.Equal("Retroceder.", replies[RaidChest.StepBack]);
            Assert.Equal("Retroceder.", replies[RaidChest.StepBackFromWarning]);

            // Con las manos vacías no se ofrece vaciarlas.
            Assert.Equal(new[] { RaidChest.Approach, RaidChest.StepBack }, RaidChest.FirstReplies(false));
            Assert.Equal(new[] { RaidChest.DropTreasures, RaidChest.Approach, RaidChest.StepBack },
                         RaidChest.FirstReplies(true));
            Assert.Equal(new[] { RaidChest.TakeAndRun, RaidChest.StepBackFromWarning },
                         RaidChest.WarningReplies());

            Assert.True(RaidChest.Owns(RaidChest.TakeAndRun));
            Assert.False(RaidChest.Owns(Luminomachine.DontTouchReply));
        }

        /// <summary>
        /// Y se va llenando. Los cinco tramos son los del aspecto de su propia plantilla, y se
        /// eligen con el mismo evaluador de criterios que todo lo demás de la raid.
        /// </summary>
        [Fact]
        public void The_chest_fills_up_as_the_score_rises()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            string look = ChestLook();
            var variants = Npcs.Variantes(look);
            Assert.Equal(5, variants.Count);
            Assert.Equal(RaidChest.Tiers.Count, variants.Count);

            for (int i = 0; i < variants.Count; i++)
            {
                Assert.Equal(RaidChest.Tiers[i].Bones, variants[i].Bones);
                Assert.Contains("Raid_Score", variants[i].Criterion);
            }

            // Un cofre con 20.000 puntos es el tercero, y el criterio del tercero es el único que
            // se cumple con esa puntuación.
            var raid = new RaidInstance(1, Raids.Gigalodon, 1, 1, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            raid.Set(RaidInstance.ScoreVariable, 20_000);
            var resolver = raid.ResolverFor(1136);

            var cumplidos = variants.Where(v => Criterion.Met(v.Criterion, resolver)).ToList();
            Assert.Single(cumplidos);
            Assert.Equal(RaidChest.Tiers[2].Bones, cumplidos[0].Bones);
            Assert.Equal(2, RaidChest.TierOf(20_000));

            // Los bordes, que son exactos: 4.999 es el primero y 5.000 ya es el segundo.
            Assert.Equal(0, RaidChest.TierOf(0));
            Assert.Equal(0, RaidChest.TierOf(4_999));
            Assert.Equal(1, RaidChest.TierOf(5_000));
            Assert.Equal(3, RaidChest.TierOf(44_999));
            Assert.Equal(4, RaidChest.TierOf(45_000));
            Assert.Equal(4, RaidChest.TierOf(1_000_000));

            // Fuera de una raid no se sabe la puntuación, así que no gana ninguno y se queda el
            // de por defecto: el cofre vacío.
            Assert.DoesNotContain(variants, v => Criterion.Met(v.Criterion, _ => Answer.Unknown));
        }

        /// <summary>
        /// El lector de aspectos, que antes se tragaba los cinco de una vez: cortaba por el primer
        /// corchete y el último, así que de una plantilla de varios salía un aspecto imposible.
        /// </summary>
        [Fact]
        public void A_look_with_several_variants_is_read_one_by_one()
        {
            // Uno solo, y sin criterio: lo normal en 6.419 de las 6.467 plantillas.
            var solo = Npcs.Variantes("{10107}");
            Assert.Single(solo);
            Assert.Equal(10107, solo[0].Bones);
            Assert.Equal("", solo[0].Criterion);

            // Con un aspecto DENTRO -la montura de un NPC- las comas de dentro no separan nada.
            var conMontura = Npcs.Variantes("{1|41,2068,1369|4=#191919|52|1@0={195|||110}}");
            Assert.Single(conMontura);
            Assert.Equal(1, conMontura[0].Bones);
            Assert.Equal(new long[] { 41, 2068, 1369 }, conMontura[0].Skins);

            // Y el caso de los otros 47: el primero sin criterio, los demás con el suyo.
            var evento = Npcs.Variantes("{9269|||50$1;0;0;},{9269|||50$2;0;0;WE=228|WE=252}");
            Assert.Equal(2, evento.Count);
            Assert.Equal("", evento[0].Criterion);
            Assert.Equal("WE=228|WE=252", evento[1].Criterion);
        }

        /// <summary>Las frases y las respuestas del 7861, tal cual las tiene la base.</summary>
        private static (Dictionary<long, string> Messages, Dictionary<long, string> Replies) ChestTexts()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();

            var template = connection.CreateCommand();
            template.CommandText = "SELECT Data FROM NpcTemplates WHERE Id = $id;";
            template.Parameters.AddWithValue("$id", RaidChest.NpcId);
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

        private static string ChestLook()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            var query = connection.CreateCommand();
            query.CommandText = "SELECT Look FROM NpcTemplates WHERE Id = $id;";
            query.Parameters.AddWithValue("$id", RaidChest.NpcId);
            return query.ExecuteScalar() as string ?? "";
        }
    }

    /// <summary>
    /// Dónde acaban puestos los cofres. Toca el estado estático de MapManager.
    /// </summary>
    [Collection("MapManager")]
    public class RaidChestPlacementTests
    {
        [Fact]
        public void One_chest_at_the_far_end_of_each_raid()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            var antes = MapManager.WalkableCells;
            try
            {
                var casillas = new Dictionary<long, List<int>>();
                foreach (var kind in Raids.All)
                {
                    long map = DatabaseManager.MapsOfSubArea(kind.Floors[^1]).Min();
                    casillas[map] = new List<int> { 11, 300 };
                }

                MapManager.WalkableCells = casillas;
                RaidChests.Place();

                Assert.Equal(2, RaidChests.Placed.Count);
                foreach (var chest in RaidChests.Placed)
                {
                    var kind = Raids.Of(chest.RaidId);
                    Assert.Equal(kind.Floors[^1], chest.SubArea);
                    Assert.Equal(chest.SubArea, DatabaseManager.SubAreaOfMap(chest.MapId));
                    Assert.Contains(chest.Cell, casillas[chest.MapId]);
                    Assert.Equal(chest.RaidId, RaidChests.RaidOn(chest.MapId));
                }

                // El de la Sima está en la sexta planta, la única sin luz: la que se acaba, no la
                // que se ilumina.
                var sima = RaidChests.Placed.Single(c => c.RaidId == Raids.Gigalodon);
                Assert.Equal(1136, sima.SubArea);
                Assert.Equal(0, Luminomachines.FloorOn(sima.MapId));
            }
            finally
            {
                MapManager.WalkableCells = antes;
                RaidChests.Forget();
            }
        }
    }

    /// <summary>
    /// Soltar los tesoros, tomar el cofre y la clasificación de la semana.
    /// </summary>
    [Collection("guild raids")]
    public class RaidLadderTests : IDisposable
    {
        private readonly string _file;

        public RaidLadderTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-cofre-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
            GuildRaidManager.Forget();
        }

        public void Dispose()
        {
            GuildRaidManager.Forget();
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static RaidInstance Running(long characterId, string name, int raidId = 1)
        {
            var guild = GuildStore.Create(characterId, name, 165, 8, 16744448, 9476018);
            var raid = new RaidInstance(characterId, raidId, guild.Id, characterId,
                                        DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            raid.Add(characterId);
            GuildRaidManager.Remember(raid);
            return raid;
        }

        /// <summary>Los tesoros suben la puntuación del cofre por lo que valen.</summary>
        [Fact]
        public void Treasures_raise_the_score_by_what_they_are_worth()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            var raid = Running(7001, "Jondo");
            Assert.Equal(0, RaidChests.ScoreOf(7001));

            long ahora = RaidChests.Drop(7001, new Dictionary<int, int> { [32465] = 10, [32470] = 1 });
            Assert.Equal(50, ahora);                       // 10 Quartz a 2 y un Ónix a 30
            Assert.Equal(50, raid.Score);

            RaidChests.Drop(7001, new Dictionary<int, int> { [34336] = 1 });
            Assert.Equal(10_050, raid.Score);
            Assert.Equal(1, RaidChest.TierOf(raid.Score));

            // Fuera de una raid no hay cofre en el que soltarlos.
            Assert.Equal(-1, RaidChests.Drop(9999, new Dictionary<int, int> { [32465] = 1 }));
            Assert.Equal(-1, RaidChests.ScoreOf(9999));
        }

        /// <summary>Tomar el cofre acaba la raid para todos y deja la puntuación apuntada.</summary>
        [Fact]
        public async System.Threading.Tasks.Task Taking_the_chest_ends_the_raid_for_everyone()
        {
            var raid = Running(7001, "Jondo");
            raid.Add(7002);
            raid.Set(RaidInstance.ScoreVariable, 31_000);

            Assert.Equal(31_000, await RaidChests.TakeAsync(7001));
            Assert.False(raid.Running);
            Assert.Equal(RaidInstance.Ending.Beaten, raid.Over);
            Assert.Null(GuildRaidManager.RunningOf(7001));

            var tabla = GuildStore.Ladder(Raids.Gigalodon, DateTimeOffset.UtcNow);
            Assert.Single(tabla);
            Assert.Equal(31_000, tabla[0].Score);
            Assert.Equal("Jondo", tabla[0].Name);
            Assert.Equal(1, tabla[0].Place);
        }

        /// <summary>
        /// La clasificación se queda con la MEJOR de la semana, no con la suma, y a igual
        /// puntuación manda quien la hizo antes.
        /// </summary>
        [Fact]
        public void The_ladder_keeps_the_best_of_the_week()
        {
            var lunes = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);   // un miércoles
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            var otro = GuildStore.Create(7002, "Rivales", 165, 8, 16744448, 9476018);

            Assert.Equal(12_000, GuildStore.RecordRaidScore(guild.Id, 1, 12_000, lunes));
            Assert.Equal(12_000, GuildStore.RecordRaidScore(guild.Id, 1, 4_000, lunes.AddMinutes(30)));
            Assert.Equal(20_000, GuildStore.RecordRaidScore(guild.Id, 1, 20_000, lunes.AddHours(2)));

            var tabla = GuildStore.Ladder(1, lunes);
            Assert.Single(tabla);
            Assert.Equal(20_000, tabla[0].Score);
            Assert.Equal(3, tabla[0].Runs);

            // Un empate: gana quien llegó antes, que es lo único que deja un orden estable.
            GuildStore.RecordRaidScore(otro.Id, 1, 20_000, lunes.AddHours(3));
            tabla = GuildStore.Ladder(1, lunes);
            Assert.Equal(2, tabla.Count);
            Assert.Equal("Jondo", tabla[0].Name);
            Assert.Equal("Rivales", tabla[1].Name);
            Assert.Equal(1, GuildStore.PlaceOf(guild.Id, 1, lunes));
            Assert.Equal(2, GuildStore.PlaceOf(otro.Id, 1, lunes));

            // Cada raid lleva la suya, y cada semana la suya.
            Assert.Empty(GuildStore.Ladder(2, lunes));
            Assert.Empty(GuildStore.Ladder(1, lunes.AddDays(7)));
            Assert.Equal(0, GuildStore.PlaceOf(guild.Id, 1, lunes.AddDays(7)));

            // La semana gira el martes: el lunes de antes es todavía la semana anterior.
            Assert.Equal("2026-09-15", GuildStore.WeekOf(lunes));
            Assert.Equal("2026-09-08", GuildStore.WeekOf(lunes.AddDays(-2)));
        }

        /// <summary>El podio son los tres ornamentos que el cliente trae con el nombre de la raid.</summary>
        [Fact]
        public void The_podium_is_the_three_ornaments_of_the_raid()
        {
            Assert.Equal(new[] { 184, 185, 186 }, Raids.Of(Raids.Gigalodon).Podium);
            Assert.Equal(new[] { 181, 182, 183 }, Raids.Of(Raids.EternalGardens).Podium);
        }
    }
}
