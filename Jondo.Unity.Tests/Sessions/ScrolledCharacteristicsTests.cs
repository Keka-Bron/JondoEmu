using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// Los pergaminos van aparte de los puntos: en su campo de la trama, en su columna de la base,
    /// y fuera de la cuenta de lo gastado.
    /// </summary>
    /// <remarks>
    /// Un nivel 200 recién hecho tenía 183 puntos por repartir en vez de 995 porque la creación
    /// metía los pergaminos en la base, y la base son los puntos gastados. El campo es el f3 de
    /// cada característica, medido en 156 capturas de personajes reales -siempre 100, nunca 101-,
    /// al lado del f2 de los puntos y del f7 del equipo.
    /// </remarks>
    public class ScrolledCharacteristicsTests
    {
        /// <summary>
        /// La hoja lleva los puntos en f2 y los pergaminos en f3, como la fuerza real de la captura:
        /// <c>f4 { f2: 398, f3: 100, f7: 499 }</c>.
        /// </summary>
        [Fact]
        public void The_sheet_sends_points_and_scrolls_in_different_fields()
        {
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                session.State.CharacterLevel = 200;
                session.State.StatStrength = 398;
                session.State.ScrolledStrength = 100;
                session.State.StatVitality = 0;
                session.State.ScrolledVitality = 100;
                session.State.StatWisdom = 0;
                session.State.ScrolledWisdom = 0;

                var kub = ProtoMessage.Parse(ConnectionProtocol.BuildCharacteristics());
                var body = ProtoMessage.Parse(kub.Fields.Single(f => f.FieldNumber == 2).BytesValue);

                var strength = Detail(body, ConnectionProtocol.Stat.Strength);
                Assert.Equal(398, Value(strength, 2));
                Assert.Equal(100, Value(strength, 3));

                // Sin puntos y con pergaminos: sólo el f3, que es lo que hace un proto3 con un cero.
                var vitality = Detail(body, ConnectionProtocol.Stat.Vitality);
                Assert.Equal(0, Value(vitality, 2));
                Assert.Equal(100, Value(vitality, 3));

                // Y sin ninguna de las dos cosas, nada de nada.
                var wisdom = Detail(body, ConnectionProtocol.Stat.Wisdom);
                Assert.Equal(0, Value(wisdom, 2));
                Assert.Equal(0, Value(wisdom, 3));

                // Lo que el juego usa es la suma: 498 de fuerza son 3.490 pods.
                Assert.Equal(498, session.State.TotalStrength);
                Assert.Equal(1000 + 5 * 498, Value(Detail(body, ConnectionProtocol.Stat.Pods), 2));
            }
        }

        /// <summary>
        /// La migración: los que nacieron con 101 en las seis de la base los pierden, se quedan con
        /// los pergaminos en su columna y recuperan todo su capital. Los demás no se tocan.
        /// </summary>
        [Fact]
        public void The_migration_moves_the_scrolls_out_of_the_base_once()
        {
            string file = Path.Combine(Path.GetTempPath(), $"jondo-pergaminos-{Guid.NewGuid():N}.db");
            try
            {
                using (var connection = new SqliteConnection($"Data Source={file}"))
                {
                    connection.Open();
                    var create = connection.CreateCommand();
                    create.CommandText = @"
                        CREATE TABLE Characters (
                            Id INTEGER PRIMARY KEY, Name TEXT, Level INTEGER, RemainingPoints INTEGER,
                            Vitality INTEGER, Wisdom INTEGER, Strength INTEGER,
                            Intelligence INTEGER, Chance INTEGER, Agility INTEGER);
                        INSERT INTO Characters VALUES (1, 'Test',      200, 183, 101, 101, 101, 101, 101, 101);
                        INSERT INTO Characters VALUES (2, 'Terceron',    3,  10, 101, 101, 101, 101, 101, 101);
                        INSERT INTO Characters VALUES (3, 'Keka',      201,   0,   3,   0, 398,   0,   0,   0);
                        INSERT INTO Characters VALUES (4, 'Dragon',    200, 825,   0,   0,  50,  40,  30,  50);";
                    create.ExecuteNonQuery();

                    DatabaseManager.MoveScrollsOutOfTheBase(connection);

                    var read = connection.CreateCommand();
                    read.CommandText = "SELECT Id, RemainingPoints, Strength, Vitality, ScrolledStrength, ScrolledVitality " +
                                       "FROM Characters ORDER BY Id;";
                    using var rows = read.ExecuteReader();

                    // Test: los 101 fuera, 995 por repartir, pergaminos a 100.
                    Assert.True(rows.Read());
                    Assert.Equal(995, rows.GetInt32(1));
                    Assert.Equal(0, rows.GetInt32(2));
                    Assert.Equal(0, rows.GetInt32(3));
                    Assert.Equal(100, rows.GetInt32(4));
                    Assert.Equal(100, rows.GetInt32(5));

                    // Terceron, nivel 3: diez puntos, que son sus 5 x 2.
                    Assert.True(rows.Read());
                    Assert.Equal(10, rows.GetInt32(1));
                    Assert.Equal(0, rows.GetInt32(2));

                    // Keka, que repartió de verdad: la base se queda, y pergaminos a 100 igual, que
                    // es lo que su captura enseña en el f3.
                    Assert.True(rows.Read());
                    Assert.Equal(0, rows.GetInt32(1));
                    Assert.Equal(398, rows.GetInt32(2));
                    Assert.Equal(3, rows.GetInt32(3));
                    Assert.Equal(100, rows.GetInt32(4));

                    Assert.True(rows.Read());
                    Assert.Equal(825, rows.GetInt32(1));
                    Assert.Equal(50, rows.GetInt32(2));
                    rows.Close();

                    // La segunda vez no hace nada: las columnas ya están y la limpieza va atada a
                    // crearlas. Un personaje que después reparta hasta tener 101 en las seis no
                    // se lo ve borrar.
                    var later = connection.CreateCommand();
                    later.CommandText = "UPDATE Characters SET Vitality = 101, Wisdom = 101, Strength = 101, " +
                                        "Intelligence = 101, Chance = 101, Agility = 101, RemainingPoints = 7 WHERE Id = 4;";
                    later.ExecuteNonQuery();

                    DatabaseManager.MoveScrollsOutOfTheBase(connection);

                    var again = connection.CreateCommand();
                    again.CommandText = "SELECT Strength, RemainingPoints FROM Characters WHERE Id = 4;";
                    using var row = again.ExecuteReader();
                    Assert.True(row.Read());
                    Assert.Equal(101, row.GetInt32(0));
                    Assert.Equal(7, row.GetInt32(1));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { File.Delete(file); } catch (IOException) { }
            }
        }

        private static ProtoMessage Detail(ProtoMessage body, int statId)
        {
            foreach (var field in body.Fields)
            {
                if (field.FieldNumber != 11 || field.WireType != 2) continue;
                var entry = ProtoMessage.Parse(field.BytesValue);
                long id = entry.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 0)?.VarIntValue ?? 0;
                if (id != statId) continue;
                var detail = entry.Fields.FirstOrDefault(f => f.FieldNumber == 4 && f.WireType == 2);
                return detail == null ? new ProtoMessage() : ProtoMessage.Parse(detail.BytesValue);
            }

            return new ProtoMessage();
        }

        private static long Value(ProtoMessage detail, int field)
            => detail.Fields.FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;
    }
}
