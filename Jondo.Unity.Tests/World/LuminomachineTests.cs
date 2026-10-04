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
    /// La luminomáquina, contra el catálogo del propio cliente: la escalera de sal, qué respuestas
    /// se ofrecen con cuánta luz, y que cada id que se manda dibuja en pantalla la frase que
    /// decimos que dibuja.
    /// </summary>
    public class LuminomachineTests
    {
        /// <summary>Cómo llama el cliente a cada franja de luz.</summary>
        private static readonly string[] Bands = { "", "primera", "segunda", "tercera", "última" };

        /// <summary>
        /// La escalera: una franja más cuesta 1, 3, 6 y 10, y un salto paga la suma.
        /// </summary>
        /// <remarks>
        /// No es una tabla inventada. Va escrita en el aviso del propio objeto —«Cada tramo de luz
        /// adicional requiere más sal (1-3-6-10)»— y las diez respuestas de depósito de la máquina
        /// dicen exactamente estas diez sumas.
        /// </remarks>
        [Fact]
        public void The_ladder_is_one_three_six_ten()
        {
            Assert.Equal(new[] { 1, 3, 6, 10 }, Luminomachine.Steps);

            Assert.Equal(1, Luminomachine.Cost(0, 1));
            Assert.Equal(4, Luminomachine.Cost(0, 2));
            Assert.Equal(10, Luminomachine.Cost(0, 3));
            Assert.Equal(20, Luminomachine.Cost(0, 4));
            Assert.Equal(3, Luminomachine.Cost(1, 2));
            Assert.Equal(9, Luminomachine.Cost(1, 3));
            Assert.Equal(19, Luminomachine.Cost(1, 4));
            Assert.Equal(6, Luminomachine.Cost(2, 3));
            Assert.Equal(16, Luminomachine.Cost(2, 4));
            Assert.Equal(10, Luminomachine.Cost(3, 4));

            // Hacia atrás no se va, y de la última no se pasa.
            Assert.Equal(0, Luminomachine.Cost(2, 1));
            Assert.Equal(0, Luminomachine.Cost(3, 5));
        }

        /// <summary>
        /// Sólo se ofrece lo que se puede pagar: un botón que no puede funcionar es peor que no
        /// tener botón. Y cuando no llega ni para una franja, la máquina dice cuánta sal falta.
        /// </summary>
        [Fact]
        public void Only_what_can_be_paid_is_offered()
        {
            Assert.Equal(new[] { Luminomachine.FetchReply(1, 0), Luminomachine.DontTouchReply },
                         Luminomachine.RepliesFor(1, 0, 0));

            Assert.Equal(new[]
            {
                Luminomachine.DepositReply(1, 0, 1),
                Luminomachine.DepositReply(1, 0, 2),
                Luminomachine.DontTouchReply,
            }, Luminomachine.RepliesFor(1, 0, 4));

            // Con veinte sales caben las cuatro franjas de golpe.
            Assert.Equal(5, Luminomachine.RepliesFor(1, 0, 20).Count);

            // Desde la segunda sólo quedan dos saltos, y con nueve sales entran los dos.
            Assert.Equal(new[]
            {
                Luminomachine.DepositReply(4, 2, 3),
                Luminomachine.DepositReply(4, 2, 4),
                Luminomachine.DontTouchReply,
            }, Luminomachine.RepliesFor(4, 2, 16));

            // Encendida del todo no pide nada, y lo dice con la frase de la que ya arde.
            Assert.Equal(new[] { Luminomachine.DontTouchReply }, Luminomachine.RepliesFor(1, 4, 100));
            Assert.Equal(Luminomachine.LitMessage, Luminomachine.MessageAt(1, 4));
            Assert.Equal(60276, Luminomachine.MessageAt(1, 0));
            Assert.Equal(60280, Luminomachine.MessageAt(5, 3));
        }

        /// <summary>
        /// Una respuesta dice de qué máquina viene y qué salto compra, y las que no compran nada
        /// también se reconocen: hace falta para cerrar la conversación en vez de dejarla caer en
        /// el camino normal, que buscaría un árbol de diálogo que esta máquina no tiene.
        /// </summary>
        [Fact]
        public void A_reply_says_which_machine_and_which_jump()
        {
            for (int floor = 1; floor <= Luminomachine.Machines; floor++)
            {
                for (int from = 0; from < Luminomachine.MostLight; from++)
                {
                    for (int to = from + 1; to <= Luminomachine.MostLight; to++)
                    {
                        var read = Luminomachine.Read(Luminomachine.DepositReply(floor, from, to));
                        Assert.NotNull(read);
                        Assert.Equal(floor, read!.Value.Floor);
                        Assert.Equal(from, read.Value.From);
                        Assert.Equal(to, read.Value.To);
                        Assert.Equal(Luminomachine.Cost(from, to), read.Value.Cost);
                        Assert.True(read.Value.Buys);
                    }

                    var fetch = Luminomachine.Read(Luminomachine.FetchReply(floor, from));
                    Assert.Equal(floor, fetch!.Value.Floor);
                    Assert.Equal(Luminomachine.Steps[from], fetch.Value.Cost);
                    Assert.False(fetch.Value.Buys);
                }
            }

            Assert.False(Luminomachine.Read(Luminomachine.DontTouchReply)!.Value.Buys);

            // «Hasta luego.», la despedida que llevan sesenta y dos NPCs, no es suya.
            Assert.Null(Luminomachine.Read(7846));
            Assert.False(Luminomachine.Owns(7846));
        }

        /// <summary>
        /// Y la de verdad: cada id que mandamos dibuja en pantalla la frase que decimos.
        /// </summary>
        /// <remarks>
        /// El cliente resuelve una respuesta a un texto por su id, así que una tabla desplazada una
        /// casilla no da error en ninguna parte: el jugador lee «Dejar 10 sales» y se le cobran 4.
        /// Esto vuelve a leer las setenta y seis respuestas de la plantilla del 8007 en world.db y
        /// las compara con lo que dice el código.
        /// </remarks>
        [Fact]
        public void The_reply_table_matches_what_the_client_will_draw()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            var (messages, replies) = MachineTexts();
            Assert.Equal(76, replies.Count);

            for (int floor = 1; floor <= Luminomachine.Machines; floor++)
            {
                for (int from = 0; from < Luminomachine.MostLight; from++)
                {
                    for (int to = from + 1; to <= Luminomachine.MostLight; to++)
                    {
                        string text = replies[Luminomachine.DepositReply(floor, from, to)];
                        Assert.StartsWith("Dejar " + Luminomachine.Cost(from, to) + " sal", text);
                        Assert.Contains(Bands[to] + " franja", text);
                    }

                    string fetch = replies[Luminomachine.FetchReply(floor, from)];
                    Assert.StartsWith("Ir a recoger " + Luminomachine.Steps[from] + " sal", fetch);
                    Assert.Contains("franja de luz siguiente", fetch);
                }
            }

            Assert.Equal("No tocar la máquina.", replies[Luminomachine.DontTouchReply]);

            // Y las frases: la de la que ya arde es la única distinta de las seis, que es lo que
            // la hace ser la de una planta a la que no le falta sal.
            Assert.Contains("alimentada por la energía de la sal", messages[Luminomachine.LitMessage]);
            for (int floor = 1; floor <= Luminomachine.Machines; floor++)
            {
                Assert.Equal("*se pone a temblar y a zumbar*", messages[Luminomachine.MessageFor(floor)]);
            }
        }

        /// <summary>
        /// El botín de la raid no está en la tabla del monstruo, está en la GLOBAL: la Madrepeora
        /// no tiene ni una fila propia y tiene nueve ahí, con la sal al 30 % y las siete gemas.
        /// </summary>
        [Fact]
        public void The_raid_loot_lives_in_the_global_table()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            Assert.Empty(DatabaseManager.GetMonsterDrops(8324, 0));

            var global = DatabaseManager.GetMonsterGlobalDrops(8324);
            Assert.Equal(9, global.Count);

            var salt = global.Single(d => d.ObjectId == Luminomachine.SaltItem);
            Assert.Equal(30, salt.PercentDrop);
            Assert.Equal("", salt.ReceiverCriterion);

            // Las siete gemas, de la más común a la más rara, sin criterio ninguna.
            Assert.Equal(new[] { 30.0, 20.0, 10.0, 5.0, 1.0, 0.1, 0.5 },
                         new[] { 32465, 32466, 32467, 32468, 32469, 32470, 32471 }
                             .Select(id => global.Single(d => d.ObjectId == id).PercentDrop));

            // El Willorque, que guarda la última planta, la suelta siempre.
            var guard = DatabaseManager.GetMonsterGlobalDrops(8252);
            Assert.Equal(100, guard.Single(d => d.ObjectId == Luminomachine.SaltItem).PercentDrop);
        }

        /// <summary>
        /// Y lo que NO se sabe no cae. El fragmento de anomalía lo lleva casi todo el juego en la
        /// misma tabla, con un criterio del que no sabemos contestar una letra; sin esta regla, el
        /// día que se leyó la tabla global empezaría a caer en todas partes.
        /// </summary>
        [Fact]
        public void What_cannot_be_answered_does_not_drop()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            var fragment = DatabaseManager.GetMonsterGlobalDrops(8324).Single(d => d.ObjectId == 26870);
            Assert.Contains("HA=50", fragment.ReceiverCriterion);

            Assert.Equal(Answer.Unknown,
                Criterion.Evaluate(fragment.ReceiverCriterion, _ => Answer.Unknown));
            Assert.False(Criterion.Met(fragment.ReceiverCriterion, _ => Answer.Unknown));
        }

        /// <summary>Las frases y las respuestas del 8007, tal cual las tiene la base.</summary>
        private static (Dictionary<long, string> Messages, Dictionary<long, string> Replies) MachineTexts()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();

            var template = connection.CreateCommand();
            template.CommandText = "SELECT Data FROM NpcTemplates WHERE Id = $id;";
            template.Parameters.AddWithValue("$id", Luminomachine.NpcId);
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
                var list = doc.RootElement.GetProperty(field).GetProperty("Array");
                foreach (var entry in list.EnumerateArray())
                {
                    var values = entry.GetProperty("values").GetProperty("Array");
                    long id = values[0].GetInt64();
                    found[id] = Text(values[1].GetInt64());
                }

                return found;
            }

            return (Pairs("dialogMessages"), Pairs("dialogReplies"));
        }
    }

    /// <summary>
    /// Dónde acaban puestas las máquinas. Toca el estado estático de MapManager, así que va con
    /// los demás que lo tocan.
    /// </summary>
    [Collection("MapManager")]
    public class LuminomachinePlacementTests
    {
        [Fact]
        public void One_machine_on_every_floor_that_has_light()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            var antes = MapManager.WalkableCells;
            try
            {
                var sima = Raids.Of(Raids.Gigalodon);

                // Casillas que NO incluyen el centro, para que se vea que se busca la más cercana
                // y no se da por buena la que se pidió.
                var casillas = new Dictionary<long, List<int>>();
                foreach (int floor in sima.Floors)
                {
                    casillas[DatabaseManager.MapsOfSubArea(floor).Min()] = new List<int> { 7, 296 };
                }

                MapManager.WalkableCells = casillas;
                Luminomachines.Place();

                Assert.Equal(5, Luminomachines.Placed.Count);
                for (int i = 0; i < Luminomachines.Placed.Count; i++)
                {
                    var machine = Luminomachines.Placed[i];
                    Assert.Equal(i + 1, machine.Floor);
                    Assert.Equal(sima.Floors[i], machine.SubArea);
                    Assert.Equal(sima.Floors[i], DatabaseManager.SubAreaOfMap(machine.MapId));
                    Assert.Contains(machine.Cell, casillas[machine.MapId]);
                    Assert.Equal(i + 1, Luminomachines.FloorOn(machine.MapId));
                }

                // La sexta planta, la del Gigalodón, no tiene variable de luz y no tiene máquina.
                Assert.Equal(6, sima.Floors.Count);
                Assert.Equal(0, Luminomachines.FloorOn(DatabaseManager.MapsOfSubArea(sima.Floors[5]).Min()));
            }
            finally
            {
                MapManager.WalkableCells = antes;
            }
        }
    }

    /// <summary>
    /// Echar la sal: la luz que compra es la de la INSTANCIA, así que hace falta una raid en
    /// marcha y estar dentro de ella.
    /// </summary>
    [Collection("guild raids")]
    public class LuminomachineDepositTests : IDisposable
    {
        private readonly string _file;

        public LuminomachineDepositTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-luz-{Guid.NewGuid():N}.db");
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

        [Fact]
        public void Salt_buys_the_floor_its_light()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            var raid = new RaidInstance(1, Raids.Gigalodon, guild.Id, 7001,
                                        DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            raid.Add(7001);
            GuildRaidManager.Remember(raid);

            Assert.Equal(0, Luminomachines.LightOn(7001, 1));

            // Cuatro sales compran dos franjas de golpe.
            Assert.Equal(2, Luminomachines.Deposit(7001, 1, 0, 2));
            Assert.Equal(2, Luminomachines.LightOn(7001, 1));
            Assert.Equal(2, raid.Get(RaidInstance.LightVariable(1)));

            // Y ya no se puede pagar desde donde la planta ya no está: es la carrera de dos que
            // hablan a la vez con la misma máquina.
            Assert.Equal(-1, Luminomachines.Deposit(7001, 1, 0, 2));
            Assert.Equal(-1, Luminomachines.Deposit(7001, 1, 2, 2));

            Assert.Equal(4, Luminomachines.Deposit(7001, 1, 2, 4));
            Assert.Equal(-1, Luminomachines.Deposit(7001, 1, 4, 4));

            // Cada planta lleva la suya: encender la primera no enciende la segunda.
            Assert.Equal(0, Luminomachines.LightOn(7001, 2));
            Assert.Equal(0, raid.Get(RaidInstance.LightVariable(2)));

            // Y la luz que se compró es la que ven los criterios del contenido. La Madrepeora anda
            // por las dos plantas con el mismo criterio escrito en world.db: en la primera, ya
            // iluminada, deja de agredir; en la segunda, a oscuras, sigue saltando encima.
            if (File.Exists(Jondo.Unity.Launcher.Paths.WorldDb))
            {
                string criterion = DatabaseManager.MonsterAggressiveImmunity(8324);
                Assert.True(Criterion.Met(criterion, raid.ResolverFor(1131)));
                Assert.False(Criterion.Met(criterion, raid.ResolverFor(1132)));
            }
        }

        [Fact]
        public void Outside_a_raid_there_is_no_light_to_buy()
        {
            GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);

            Assert.Equal(-1, Luminomachines.LightOn(7001, 1));
            Assert.Equal(-1, Luminomachines.Deposit(7001, 1, 0, 1));

            // Ni siquiera con gremio: sin raid en marcha no hay instancia que iluminar.
            Assert.Equal(-1, Luminomachines.LightOn(9999, 1));
        }
    }
}
