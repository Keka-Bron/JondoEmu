using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The accept/refuse fallback for quest givers with no authored dialogue tree.
    /// </summary>
    public sealed class FallbackQuestDialogueTests
    {
        public FallbackQuestDialogueTests()
        {
            FallbackQuestDialogue.ResetCache();
        }

        [Fact]
        public void TryPickReplies_prefers_accept_and_decline_wordings_from_the_template()
        {
            // Text keys the Translations table would return for "Aceptar" / "No, gracias".
            FallbackQuestDialogue.SeedTextKeysForTests(
                accept: new long[] { 107419 },
                decline: new long[] { 107426 });

            var template = new Npcs.Template
            {
                DialogMessageId = 11439,
                Replies = new long[] { 12706, 12715, 12710, 12716 },
                ReplyTexts = new long[] { 107544, 107419, 107426, 107417 },
            };

            Assert.True(FallbackQuestDialogue.TryPickReplies(template, out long accept, out long decline));
            Assert.Equal(12715, accept);
            Assert.Equal(12710, decline);
        }

        [Fact]
        public void TryPickReplies_falls_back_to_first_and_last_when_no_wording_matches()
        {
            FallbackQuestDialogue.SeedTextKeysForTests(
                accept: System.Array.Empty<long>(),
                decline: System.Array.Empty<long>());

            var template = new Npcs.Template
            {
                DialogMessageId = 1,
                Replies = new long[] { 10, 20, 30 },
                ReplyTexts = new long[] { 100, 200, 300 },
            };

            Assert.True(FallbackQuestDialogue.TryPickReplies(template, out long accept, out long decline));
            Assert.Equal(10, accept);
            Assert.Equal(30, decline);
        }

        [Fact]
        public void Build_returns_null_when_the_template_has_no_opening_line()
        {
            var template = new Npcs.Template
            {
                DialogMessageId = 0,
                Replies = new long[] { 1 },
                ReplyTexts = new long[] { 1 },
            };

            Assert.Null(FallbackQuestDialogue.Build(1577, 188743687, template));
        }

        [Fact]
        public void Build_returns_null_when_the_npc_has_no_replies()
        {
            var template = new Npcs.Template
            {
                DialogMessageId = 11439,
                Replies = System.Array.Empty<long>(),
                ReplyTexts = System.Array.Empty<long>(),
            };

            Assert.Null(FallbackQuestDialogue.Build(1577, 188743687, template));
        }
    }
}
