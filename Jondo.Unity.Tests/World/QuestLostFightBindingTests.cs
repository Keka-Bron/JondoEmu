using System.IO;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A beat binding may want the fight lost: «lost» marks it, «loseMap» says where the defeat
    /// leaves the character, and nothing else reads a fight as lost.
    /// </summary>
    public class QuestLostFightBindingTests
    {
        private static QuestBindingBook Book(string rows)
        {
            string path = Path.Combine(Path.GetTempPath(), $"lost_binding_{System.Guid.NewGuid():N}.json");
            File.WriteAllText(path, "{\"bindings\": [" + rows + "]}");
            try { return QuestBindingContent.Load(path); }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Un_combate_marcado_lost_se_lee_como_derrota()
        {
            var book = Book("{\"objective\": 1, \"kind\": \"beat\", \"monster\": 31, \"lost\": true}");

            var binding = book.Of(1);
            Assert.NotNull(binding);
            Assert.Equal(QuestBindingKind.Beat, binding!.Kind);
            Assert.True(binding.Lost);
        }

        [Fact]
        public void Sin_la_marca_un_combate_se_gana()
        {
            var book = Book("{\"objective\": 2, \"kind\": \"beat\", \"monster\": 31}");

            Assert.False(book.Of(2)!.Lost);
        }

        [Fact]
        public void La_derrota_lleva_a_donde_diga_loseMap()
        {
            var book = Book("{\"objective\": 3, \"kind\": \"beat\", \"monster\": 31, \"lost\": true, " +
                            "\"fight\": {\"arena\": 5, \"loseMap\": 7, \"loseCell\": 300}}");

            var fight = book.Of(3)!.Fight;
            Assert.NotNull(fight);
            Assert.Equal(7, fight!.LoseMapId);
            Assert.Equal(300, fight.LoseCellId);
            Assert.Equal(0, fight.WinMapId);
        }
    }
}
