using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The five levers of the third room of Dramak's Theatre open the arch backstage only when they
    /// are pulled in order: red, purple, orange, yellow, green (Dofus pour les noobs, «Théâtre de
    /// Dramak»). Shaca waits there and teaches the Juggle emote.
    /// </summary>
    public class DramakLeverRowTests
    {
        private const long Room3 = 118228992;
        private static readonly int[] InOrder = { 482974, 482973, 482976, 482975, 482977 };

        private static bool Pull(long character, int element)
            => InteractiveMessages.Pull(character, Room3, InteractiveMessages.Of(Room3, element)!);

        [Fact]
        public void The_levers_are_written_in_their_order()
        {
            InteractiveMessages.Forget();
            for (int i = 0; i < InOrder.Length; i++)
            {
                var said = InteractiveMessages.Of(Room3, InOrder[i]);
                Assert.NotNull(said);
                Assert.Equal(i + 1, said!.Order);
            }
            var last = InteractiveMessages.Of(Room3, InOrder[^1])!;
            Assert.Equal(482983, last.Opens);
            Assert.Contains(299, last.Frees);
        }

        [Fact]
        public void Only_the_whole_row_in_order_opens_the_arch()
        {
            InteractiveMessages.Forget();
            const long me = 1, other = 2;

            // In order: only the last one completes it.
            Assert.Equal(new[] { false, false, false, false, true }, InOrder.Select(e => Pull(me, e)).ToArray());

            // A wrong one undoes the row.
            Assert.False(Pull(me, InOrder[0]));
            Assert.False(Pull(me, InOrder[1]));
            Assert.False(Pull(me, InOrder[3]));
            Assert.False(Pull(me, InOrder[2]));
            Assert.False(Pull(me, InOrder[3]));
            Assert.False(Pull(me, InOrder[4]));

            // The first one starts it over, and each character has his own row.
            Assert.False(Pull(me, InOrder[0]));
            Assert.False(Pull(other, InOrder[0]));
            Assert.False(Pull(me, InOrder[0]));
            foreach (int e in InOrder.Skip(1).Take(3)) Assert.False(Pull(me, e));
            Assert.True(Pull(me, InOrder[4]));
            Assert.False(Pull(other, InOrder[2]));
        }
    }
}
