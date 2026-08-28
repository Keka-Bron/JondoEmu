using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The dungeon catalogue, against a real playthrough.
    /// </summary>
    /// <remarks>
    /// The room order in <c>DungeonRooms</c> was doubted for years, and the manager says so in its
    /// own class comment: the Biblioteca del Maestro Cuerbok lists its rooms at x = -14, -13, -15,
    /// which is not a progression, so nobody could tell whether the order was a walking order or
    /// just the order the client happened to serialise them in.
    ///
    /// There is now one dungeon it can be checked against.
    /// <c>Mazmorras\mazmorra de los jalatós completa</c> is somebody walking the Corte del Jalató
    /// Real from the door to the way out, and the maps they load, in order, are
    /// 121373185 → 121374209 → 121375233 → 121373187 → 121374211 — exactly the order the data
    /// lists, with a corridor between each pair. One dungeon of 187, so the doubt stands for the
    /// other 186; but the order is no longer only a guess, and if a re-extraction ever reorders
    /// this one, this says so.
    ///
    /// These read the file rather than the manager because the manager rewrites two tables of
    /// world.db as a side effect of loading, which is not a thing a test should do.
    /// </remarks>
    public class DungeonDataTests
    {
        private static bool Available => File.Exists(Paths.DungeonsJson);

        private static JsonElement Dungeon(int id, out JsonDocument doc)
        {
            doc = JsonDocument.Parse(File.ReadAllText(Paths.DungeonsJson));
            Assert.True(doc.RootElement.TryGetProperty(id.ToString(), out var dungeon),
                        $"dungeon {id} is not in the file");
            return dungeon;
        }

        private static List<long> Longs(JsonElement row, string name)
        {
            var numbers = new List<long>();
            if (!row.TryGetProperty(name, out var list)) return numbers;
            foreach (var item in list.EnumerateArray()) numbers.Add(item.GetInt64());
            return numbers;
        }

        [Fact]
        public void The_jalato_dungeon_rooms_are_in_the_order_they_are_walked()
        {
            if (!Available) return;

            var dungeon = Dungeon(1, out var doc);
            using (doc)
            {
                Assert.Equal(
                    new long[] { 121373185, 121374209, 121375233, 121373187, 121374211 },
                    Longs(dungeon, "rooms"));

                Assert.Equal(120063489, dungeon.GetProperty("entrance").GetInt64());
                Assert.Equal(120063489, dungeon.GetProperty("exit").GetInt64());
            }
        }

        [Fact]
        public void It_asks_for_its_own_key_and_takes_the_keyring()
        {
            if (!Available) return;

            // Item 1568 is "Llave de la Corte del Jalató Real". The capture shows the guardian
            // asking "¿Seguro que quieres utilizar el manojo de llaves para entrar?" before the
            // player goes in, which is what says the keyring is an alternative and not a
            // replacement.
            var dungeon = Dungeon(1, out var doc);
            using (doc)
            {
                Assert.Equal(1, dungeon.GetProperty("keyring").GetInt32());

                var required = dungeon.GetProperty("required");
                var first = required[0];
                Assert.Equal(1568, first[0].GetInt32());
                Assert.Equal(1, first[1].GetInt32());
            }
        }

        [Fact]
        public void The_boss_is_the_jalato_real()
        {
            if (!Available) return;

            var dungeon = Dungeon(1, out var doc);
            using (doc) Assert.Equal(new long[] { 147 }, Longs(dungeon, "bosses"));
        }

        [Fact]
        public void The_fields_the_extractor_used_to_throw_away_are_all_there_now()
        {
            if (!Available) return;

            // The keyring and the key were in the client's data from the start and the extractor
            // was dropping them on the floor, which is why there was no way to lock a dungeon. If
            // a future extractor stops writing them, every dungeon silently becomes open to
            // everybody — and nothing else would notice.
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.DungeonsJson));

            int withKey = 0, withKeyring = 0, withBoss = 0, total = 0;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                total++;
                if (Longs(entry.Value, "bosses").Count > 0) withBoss++;
                if (entry.Value.TryGetProperty("keyring", out var ring) && ring.GetInt32() != 0) withKeyring++;
                if (entry.Value.TryGetProperty("required", out var need) && need.GetArrayLength() > 0) withKey++;
            }

            Assert.Equal(187, total);
            Assert.True(withKey > 100, $"only {withKey} dungeons ask for a key");
            Assert.True(withKeyring > 100, $"only {withKeyring} take the keyring");
            Assert.True(withBoss > 100, $"only {withBoss} declare a boss");
        }

        [Fact]
        public void Every_dungeon_has_somewhere_to_start_and_somewhere_to_come_out()
        {
            if (!Available) return;

            // A dungeon with no rooms cannot be entered and a dungeon with no way out is a trap.
            // The exit falls back to the entrance, which is the same map in 152 of the 187.
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.DungeonsJson));

            var roomless = new List<string>();
            var wayless = new List<string>();
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (Longs(entry.Value, "rooms").Count == 0) roomless.Add(entry.Name);

                long exit = entry.Value.GetProperty("exit").GetInt64();
                long entrance = entry.Value.GetProperty("entrance").GetInt64();
                if (exit == 0 && entrance == 0) wayless.Add(entry.Name);
            }

            Assert.True(roomless.Count == 0, "dungeons with no rooms: " + string.Join(", ", roomless));
            Assert.True(wayless.Count == 0, "dungeons with no way out: " + string.Join(", ", wayless));
        }
    }
}
