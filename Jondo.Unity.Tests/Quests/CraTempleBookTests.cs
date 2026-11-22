using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// «Devoción a Ocra» (762), «Lee el libro del templo» (objective 4846): the client's own objective
    /// row places it on map 183765006, the room of the book about the god, not on 183765000 where the
    /// lore book stands. 511678 is that map's only element with the god books' drawing (29642).
    /// </summary>
    public sealed class CraTempleBookTests
    {
        [Fact]
        public void The_objective_is_the_book_about_the_god_on_the_client_s_map()
        {
            QuestEngine.Load();
            var binding = QuestEngine.Bindings.Of(4846);
            Assert.NotNull(binding);
            Assert.Equal(new[] { (183765006L, 511678) }, binding!.Elements.ToArray());
        }

        [Fact]
        public void The_book_it_binds_opens_a_document()
        {
            Readables.Forget();
            Assert.Equal(229, Readables.Of(183765006, 511678)?.Document);   // «CRA»
        }
    }
}
