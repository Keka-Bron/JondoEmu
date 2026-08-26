using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.World.Client;
using Xunit;

namespace Jondo.Unity.Tests.Studio
{
    /// <summary>
    /// What an NPC can say, and which number of it travels.
    /// </summary>
    /// <remarks>
    /// These run against <c>world.db</c> and the client on this machine, and skip when neither is
    /// here. The one that matters is the id: a dialogue tree keyed on the wrong number produces an
    /// editor that looks completely right and an NPC that says something else.
    /// </remarks>
    public class NpcCatalogueTests
    {
        private static bool Here => File.Exists(Paths.WorldDb);

        private static NpcCatalogue Open()
            => new NpcCatalogue(ClientText.Open(Paths.ClientTextFile("es"), GameLanguage.Spanish));

        /// <summary>
        /// The id a line is filed under has to be the one the server puts on the wire.
        /// </summary>
        /// <remarks>
        /// <c>dialogData</c> carries two numbers per line and they are different: Snori Nairb's
        /// first is <c>id 3312</c>, <c>messageId 6169</c>. The captured <c>ios</c> that opens his
        /// dialogue carries <b>6169</b>. Filing the tree under 3312 — which this did until it was
        /// caught — makes every authored NPC say some other line, with nothing on screen to say so.
        /// </remarks>
        [Fact]
        public void A_line_is_filed_under_the_number_that_travels()
        {
            if (!Here) return;

            using var catalogue = Open();
            var snori = catalogue.Source(1088);
            if (snori == null) return;

            Assert.Contains(snori.Messages, line => line.Id == 6169);
            Assert.DoesNotContain(snori.Messages, line => line.Id == 3312);
        }

        [Fact]
        public void An_npc_brings_its_lines_and_its_replies()
        {
            if (!Here) return;

            using var catalogue = Open();
            var snori = catalogue.Source(1088);
            if (snori == null) return;

            Assert.Equal(3, snori.Messages.Count);
            Assert.True(snori.Replies.Count > 30,
                        $"Snori Nairb should carry 39 replies, he carries {snori.Replies.Count}");
        }

        /// <summary>
        /// The lines come out with their words on them, which is the whole reason a dialogue can be
        /// decided at all. Numbers alone are undecidable.
        /// </summary>
        [Fact]
        public void The_words_are_there_when_the_client_is()
        {
            if (!Here || !File.Exists(Paths.ClientTextFile("es"))) return;

            using var catalogue = Open();
            var snori = catalogue.Source(1088);
            if (snori == null || catalogue.MessageKeys == 0) return;

            Assert.All(snori.Messages.Take(1), line => Assert.NotEqual("", line.Text));
            Assert.Contains(snori.Replies, reply => reply.Text.Length > 0);
        }

        /// <summary>
        /// Any line in the game, not only an NPC's own. The <c>ios</c> the server sends carries the
        /// line as a plain id into the game's table with nothing tying it to the speaker, so this
        /// is what makes "have him say something else" possible at all.
        /// </summary>
        [Fact]
        public void Every_line_in_the_game_is_offered()
        {
            if (!Here) return;

            using var catalogue = Open();
            if (catalogue.MessageKeys == 0) return;

            var all = catalogue.EveryLine();
            Assert.True(all.Count > 40_000, $"only {all.Count} lines in the game, which is too few");
            Assert.All(all.Take(50), line => Assert.NotEqual("", line.Text));
        }

        [Fact]
        public void Every_reply_in_the_game_is_offered()
        {
            if (!Here) return;

            using var catalogue = Open();
            var all = catalogue.EveryReply();

            Assert.True(all.Count > 5_000, $"only {all.Count} replies in the game, which is too few");
        }
    }
}
