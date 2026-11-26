using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// «Le guerrier noir» (1914): the book at the back of Gisgul's room (511310) is a document and the
    /// Bwork bibliophile's (5004) fight at once. The fight comes first and marks its chain done, so the
    /// second click, after the win, reads the book.
    /// </summary>
    public sealed class GisgulBookFightTests
    {
        private const long Room = 176817155;
        private const int Book = 511310;

        [Fact]
        public void The_book_is_read_only_after_its_fight()
        {
            var fight = NpcFights.OnMap(Room).FirstOrDefault(f => f.ElementId == Book);
            if (fight == null) return;   // no quest data
            Assert.NotNull(Readables.Of(Room, Book));
            Assert.Contains(fight.Monsters, m => m.Monster == 5004);
            Assert.NotEqual(0, fight.Final.Marks);
            // Once its mark is there, the element no longer starts it and the reading takes over.
            Assert.True(fight.Done(mark => mark == fight.Final.Marks));
            Assert.Null(NpcFights.ForElement(Room, Book, mark => mark == fight.Final.Marks));
        }
    }
}
