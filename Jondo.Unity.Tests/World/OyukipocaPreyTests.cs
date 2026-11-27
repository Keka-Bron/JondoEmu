using System;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Quest 2080: Oyukipoca's prey (NPC 4998) shows in a won room of a Paradox or Nightmare dream
    /// once the key was handed in, while finding the spirit is still pending.
    /// </summary>
    public class OyukipocaPreyTests
    {
        private static Dreams.Sala Room(int id, int row, bool final = false)
        {
            var room = new Dreams.Sala { Id = id, Fila = row, EsFinal = final };
            room.Miembros.Add((492, 1));
            return room;
        }

        private static Dreams.Sueno Dream(int difficulty, bool souvenir = false)
            => new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = difficulty, Seed = "ABCDEFGH", FromSouvenir = souvenir };

        private static readonly Func<int, bool> Pending = o => o == Dreams.OyukipocaPreyObjective;
        private static readonly Func<int, bool> KeyGiven = o => o == Dreams.OyukipocaKeyObjective;

        private static int Shown(Dreams.Sueno dream, Func<int, bool> pending, Func<int, bool> done, int row = 2)
            => Enumerable.Range(1, 40).Select(id => Dreams.OyukipocaPreyOf(dream, Room(id, row), pending, done))
                         .FirstOrDefault(npc => npc != 0);

        [Theory]
        [InlineData(4)]
        [InlineData(7)]
        [InlineData(10)]
        public void The_prey_shows_in_a_paradox_or_nightmare(int difficulty)
            => Assert.Equal(Dreams.OyukipocaPrey, Shown(Dream(difficulty), Pending, KeyGiven));

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public void Never_in_a_plain_dream(int difficulty)
            => Assert.Equal(0, Shown(Dream(difficulty), Pending, KeyGiven));

        [Fact]
        public void Only_after_the_key_and_while_the_spirit_is_to_find()
        {
            Assert.Equal(0, Shown(Dream(5), Pending, _ => false));
            Assert.Equal(0, Shown(Dream(5), _ => false, KeyGiven));
        }

        [Fact]
        public void Not_in_a_souvenir_nor_the_first_row_nor_the_end()
        {
            Assert.Equal(0, Shown(Dream(5, souvenir: true), Pending, KeyGiven));
            Assert.Equal(0, Shown(Dream(5), Pending, KeyGiven, row: 0));
            Assert.Equal(0, Dreams.OyukipocaPreyOf(Dream(5), Room(1, 26, final: true), Pending, KeyGiven));
        }

        [Fact]
        public void The_prey_counts_for_finding_the_spirit()
            => Assert.Equal(Dreams.OyukipocaPreyObjective, Dreams.ObjectiveOfRoomNpc(Dreams.OyukipocaPrey));
    }
}
