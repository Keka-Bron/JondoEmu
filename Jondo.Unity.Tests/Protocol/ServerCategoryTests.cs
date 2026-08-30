using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The category each server travels under, and the table of how many characters fit in each.
    /// </summary>
    /// <remarks>
    /// Written to answer one question with a measurement instead of a memory: is this server being
    /// advertised as a mono-account one, which would explain a budget of a single character?
    ///
    /// It is not. Decoded out of the raw stream of
    /// "Autenticacion-Servidor-Personaje/desde launcher a eleccion servidor.pcapng", the real
    /// server sends fourteen servers with these categories, and the account it belongs to holds
    /// ten characters across six of them with the create button ACTIVE:
    ///
    /// <code>
    ///   290 -> 1   291 -> 1   292 -> 1   293 -> 1   294 -> 1   295 -> 0
    ///   350 -> 3   351 -> 3   352 -> 3
    ///   353 -> 2   354 -> 2   355 -> 2
    ///    99 -> 4    50 -> 5
    ///   slot table: (0,5) (1,5) (2,5) (3,5) (4,5) (5,5) (6,5)
    /// </code>
    ///
    /// 290 is Tal Kasha, the one open server here, and it is category 1 in both. So the category is
    /// right and the slot table is right, and neither is what darkens that button.
    /// </remarks>
    public class ServerCategoryTests
    {
        // ------------------------------------------------------------------ protobuf, minimally

        private static (ulong Value, int Next) Varint(byte[] bytes, int at)
        {
            ulong value = 0;
            int shift = 0;
            while (true)
            {
                byte b = bytes[at++];
                value |= (ulong)(b & 0x7f) << shift;
                shift += 7;
                if ((b & 0x80) == 0) return (value, at);
            }
        }

        /// <summary>Top-level fields as (number, varint value, sub-message bytes).</summary>
        private static List<(int Number, ulong Value, byte[] Bytes)> Fields(byte[] bytes)
        {
            var found = new List<(int, ulong, byte[])>();
            int at = 0;

            while (at < bytes.Length)
            {
                var (key, afterKey) = Varint(bytes, at);
                at = afterKey;
                int number = (int)(key >> 3), wire = (int)(key & 7);

                if (wire == 0)
                {
                    var (value, after) = Varint(bytes, at);
                    at = after;
                    found.Add((number, value, Array.Empty<byte>()));
                }
                else if (wire == 2)
                {
                    var (length, after) = Varint(bytes, at);
                    at = after;
                    found.Add((number, 0, bytes.Skip(at).Take((int)length).ToArray()));
                    at += (int)length;
                }
                else break;
            }

            return found;
        }

        private static byte[] Only(byte[] bytes, int number)
            => Fields(bytes).Single(f => f.Number == number).Bytes;

        /// <summary>Down to the accepted-authentication message: f2 { f3 { f1 { here } } }.</summary>
        private static byte[] Accepted(byte[] message) => Only(Only(Only(message, 2), 3), 1);

        private static byte[] Build()
        {
            var servers = new List<DatabaseManager.DbServer>
            {
                new() { Id = 290, Type = 1 }, new() { Id = 291, Type = 1 },
                new() { Id = 292, Type = 1 }, new() { Id = 293, Type = 1 },
                new() { Id = 294, Type = 1 }, new() { Id = 295, Type = 0 },
                new() { Id = 350, Type = 3 }, new() { Id = 351, Type = 3 },
                new() { Id = 352, Type = 3 }, new() { Id = 353, Type = 2 },
                new() { Id = 354, Type = 2 }, new() { Id = 355, Type = 2 },
                new() { Id = 99, Type = 4 }, new() { Id = 50, Type = 5 },
            };

            return ConnectionProtocol.BuildAuthenticationAccepted(
                // Cuenta, apodo y tag de relleno: nada de esto se comprueba aqui, y los de una
                // cuenta real no pintan nada en un repositorio publico.
                "es", 100_000_001, "Cuenta", "0001", "2027-08-29T15:54:43+02:00",
                servers, new List<DatabaseManager.DbCharacter>());
        }

        /// <summary>Server id to category, read back out of the bytes we would put on the wire.</summary>
        private static Dictionary<int, int> Categories()
        {
            return Fields(Only(Accepted(Build()), 4))
                .Where(f => f.Number == 1)
                .Select(f => Fields(Only(f.Bytes, 1)))
                .ToDictionary(
                    identity => (int)identity.Single(g => g.Number == 1).Value,
                    identity => (int)identity.FirstOrDefault(g => g.Number == 3).Value);
        }

        private static List<(int Type, int Slots)> SlotTable()
        {
            var table = new List<(int, int)>();

            foreach (var entry in Fields(Only(Accepted(Build()), 4)).Where(f => f.Number == 2))
            {
                var inner = Fields(entry.Bytes);
                table.Add(((int)inner.FirstOrDefault(f => f.Number == 1).Value,
                           (int)inner.Single(f => f.Number == 2).Value));
            }

            return table;
        }

        // ------------------------------------------------------------------------------ the facts

        [Fact]
        public void The_open_server_travels_as_a_classic_one()
        {
            // The question that prompted this file. Tal Kasha is 290 and it goes out as category 1,
            // which is what the real server sends for that same id.
            Assert.Equal(1, Categories()[290]);
        }

        [Fact]
        public void Every_category_is_the_one_the_capture_carries()
        {
            // The whole table, so that changing one by hand fails here rather than in the client.
            var measured = new Dictionary<int, int>
            {
                [290] = 1, [291] = 1, [292] = 1, [293] = 1, [294] = 1, [295] = 0,
                [350] = 3, [351] = 3, [352] = 3,
                [353] = 2, [354] = 2, [355] = 2,
                [99] = 4, [50] = 5,
            };

            Assert.Equal(measured.OrderBy(p => p.Key), Categories().OrderBy(p => p.Key));
        }

        [Fact]
        public void The_slot_table_is_seven_categories_of_five()
        {
            // Byte for byte what the real server sends, and the reason five is not a chosen number.
            Assert.Equal(
                new List<(int, int)> { (0, 5), (1, 5), (2, 5), (3, 5), (4, 5), (5, 5), (6, 5) },
                SlotTable());
        }

        [Fact]
        public void The_category_the_open_server_uses_has_a_row_in_that_table()
        {
            // The failure this guards against: advertising a category with no slots of its own.
            // It would leave the client to invent a budget, and inventing one is exactly the
            // symptom being chased.
            Assert.Contains(SlotTable(), row => row.Type == 1 && row.Slots == 5);
        }
    }
}
