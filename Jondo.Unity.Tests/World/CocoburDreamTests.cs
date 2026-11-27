using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Quest 2037's twelve dreams of Cocobur: the first eleven are NPCs a won fight room shows,
    /// the first one the character has yet to find, gated by his level (the first six) or by an
    /// intense dream and the room's band (the next five); the twelfth is found by winning an
    /// intense dream's end.
    /// </summary>
    public class CocoburDreamTests
    {
        private static Dreams.Sala Room(int id, int row, bool final = false)
        {
            var room = new Dreams.Sala { Id = id, Fila = row, EsFinal = final };
            room.Miembros.Add((492, 1));
            return room;
        }

        private static Func<int, bool> PendingFrom(int objective)
        {
            var all = Dreams.CocoburDreams.Select(d => d.Objective).Append(Dreams.CocoburLastObjective).ToList();
            return o => all.IndexOf(o) >= all.IndexOf(objective);
        }

        /// <summary>The room that shows a dream for these, if any of the first 40 does.</summary>
        private static int Shown(Dreams.Sueno dream, int row, int level, Func<int, bool> pending)
            => Enumerable.Range(1, 40).Select(id => Dreams.CocoburDreamOf(dream, Room(id, row), level, pending))
                         .FirstOrDefault(npc => npc != 0);

        [Fact]
        public void The_first_dream_shows_at_level_130_and_not_before()
        {
            var dream = new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = 1, Seed = "ABCDEFGH" };
            Assert.Equal(4657, Shown(dream, 2, 130, PendingFrom(14613)));
            Assert.Equal(0, Shown(dream, 2, 129, PendingFrom(14613)));
        }

        [Fact]
        public void Each_dream_is_the_next_one_still_to_find()
        {
            var dream = new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = 1, Seed = "ABCDEFGH" };
            Assert.Equal(4662, Shown(dream, 2, 200, PendingFrom(14618)));
            Assert.Equal(0, Shown(dream, 2, 179, PendingFrom(14618)));
        }

        [Fact]
        public void The_later_dreams_need_an_intense_dream_and_their_band()
        {
            var easy = new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = 2, Seed = "ABCDEFGH" };
            var intense = new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = 3, Seed = "ABCDEFGH" };
            Assert.Equal(0, Shown(easy, 2, 200, PendingFrom(14619)));
            Assert.Equal(4663, Shown(intense, 2, 200, PendingFrom(14619)));
            // The eleventh only in the fifth band, rows 22 and on.
            Assert.Equal(0, Shown(intense, 21, 200, PendingFrom(14706)));
            Assert.Equal(4667, Shown(intense, 22, 200, PendingFrom(14706)));
        }

        [Fact]
        public void No_dream_in_a_souvenir_nor_at_the_end_nor_once_all_are_found()
        {
            var souvenir = new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = 3, Seed = "ABCDEFGH", FromSouvenir = true };
            Assert.Equal(0, Shown(souvenir, 2, 200, PendingFrom(14613)));
            var dream = new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = 3, Seed = "ABCDEFGH" };
            Assert.Equal(0, Dreams.CocoburDreamOf(dream, Room(1, 26, final: true), 200, PendingFrom(14613)));
            Assert.Equal(0, Shown(dream, 2, 200, _ => false));
        }

        [Fact]
        public void The_last_dream_is_found_on_an_intense_dream_end_once_the_eleven_are()
        {
            var intense = new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = 3, Seed = "ABCDEFGH" };
            var easy = new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = 2, Seed = "ABCDEFGH" };
            Assert.True(Dreams.FindsLastCocobur(intense, PendingFrom(14707)));
            Assert.False(Dreams.FindsLastCocobur(easy, PendingFrom(14707)));
            Assert.False(Dreams.FindsLastCocobur(intense, PendingFrom(14706)));
        }
    }
}
