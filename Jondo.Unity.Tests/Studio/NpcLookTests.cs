using Jondo.Unity.Sprites;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Studio.Data;
using Xunit;

namespace Jondo.Unity.Tests.Studio
{
    /// <summary>
    /// Reading an appearance: <c>{bones|skins|colours|scales}</c>.
    /// </summary>
    /// <remarks>
    /// This is the front of the chain that ends in a picture on the map. Everything after it is
    /// bundles and binary formats that fail loudly; this bit fails quietly, by picking the wrong
    /// bone and drawing somebody else.
    /// </remarks>
    public class NpcLookTests
    {
        [Fact]
        public void A_bare_bone_is_a_whole_look()
        {
            var look = NpcLook.Parse("{2713}");

            Assert.True(look.Valid);
            Assert.Equal(2713, look.Bone);
            Assert.Empty(look.Skins);
            Assert.Equal(100, look.Scale);
            Assert.False(look.Humanoid);
        }

        [Fact]
        public void The_empty_sections_in_the_middle_are_allowed()
        {
            // Snori Nairb. Three empty sections and a scale.
            var look = NpcLook.Parse("{58|||90}");

            Assert.Equal(58, look.Bone);
            Assert.Empty(look.Skins);
            Assert.Empty(look.Colours);
            Assert.Equal(90, look.Scale);
        }

        [Fact]
        public void Skins_and_colours_come_out()
        {
            var look = NpcLook.Parse("{1|100,101|1=16777215,2=#FF0000|130}");

            Assert.Equal(1, look.Bone);
            Assert.True(look.Humanoid);
            Assert.Equal(new[] { 100, 101 }, look.Skins);
            Assert.Equal(0xFFFFFF, look.Colours[1]);
            Assert.Equal(0xFF0000, look.Colours[2]);
            Assert.Equal(130, look.Scale);
        }

        /// <summary>
        /// A short hexadecimal colour is padded on the right, the way the client reads it.
        /// </summary>
        [Fact]
        public void A_short_colour_is_padded_on_the_right()
        {
            var look = NpcLook.Parse("{5|| 3=#FA0 |100}");
            Assert.Equal(0xFA0000, look.Colours[3]);
        }

        /// <summary>
        /// 135 NPCs carry more than one look, chosen by a condition on world state. The first is
        /// taken, and the fact that a choice happened is kept.
        /// </summary>
        [Fact]
        public void A_conditional_look_takes_the_first_group_and_says_so()
        {
            var look = NpcLook.Parse("{9262$1;0;0;},{9262$2;0;0;WE=228|WE=252}");

            Assert.True(look.Valid);
            Assert.Equal(9262, look.Bone);
            Assert.True(look.Conditional);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{}")]
        [InlineData("{abc}")]
        [InlineData("not a look at all")]
        public void Rubbish_comes_back_as_nothing_rather_than_throwing(string text)
            => Assert.False(NpcLook.Parse(text).Valid);

        [Fact]
        public void A_null_look_is_survivable()
            => Assert.False(NpcLook.Parse(null).Valid);

        /// <summary>
        /// The breed table, which is the one thing in the editor copied out of the client instead
        /// of read from it. 114 skins over 19 breeds.
        /// </summary>
        [Fact]
        public void The_breed_table_is_whole()
        {
            Assert.Equal(114, Breeds.Count);

            // The pairs a look actually names. 30 is a Sram, 50 an Ecaflip.
            Assert.Equal(3, Breeds.Of(30));
            Assert.Equal(5, Breeds.Of(50));
            Assert.Equal(20, Breeds.Of(3221));
            Assert.Equal(0, Breeds.Of(999999));
        }

        /// <summary>
        /// A humanoid look is drawable now. It was not, and that is what made the town screens look
        /// empty: the villagers and merchants are all bone 1.
        /// </summary>
        [Fact]
        public void A_humanoid_look_is_one_we_can_draw()
        {
            if (!Directory.Exists(Paths.ClientContentDir)) return;

            // Capitán Guarrok.
            Assert.True(NpcSprites.CanDraw("{1|30,2044,427|5=#bfa77c,2=#ffffff|43}"));
        }

        // ─── Against the real data, when it is here ───────────────────────────────

        /// <summary>
        /// Every look in the game parses, and most of them are drawable by the path that exists.
        /// </summary>
        /// <remarks>
        /// The shares measured over the 6,468 templates: 74.4% a plain numeric bone, 7.4% numeric
        /// plus skins, 16.0% the humanoid rig, 2.1% conditional. The humanoid ones are the ones
        /// this build holds back, so anything much under 80% drawable means the parser has started
        /// reading something else.
        /// </remarks>
        [Fact]
        public void Most_of_the_games_looks_are_ones_we_can_draw()
        {
            if (!File.Exists(Paths.WorldDb)) return;

            using var npcs = new NpcCatalogue();
            if (!npcs.Ready) return;

            var all = npcs.All().Where(npc => npc.Look.Length > 0).ToList();
            if (all.Count == 0) return;

            int parsed = all.Count(npc => NpcLook.Parse(npc.Look).Valid);
            Assert.True(parsed >= all.Count * 99 / 100,
                        $"only {parsed} of {all.Count} looks parsed at all");

            if (!Directory.Exists(Paths.ClientContentDir)) return;

            int drawable = all.Count(npc => NpcSprites.CanDraw(npc.Look));
            Assert.True(drawable >= all.Count * 75 / 100,
                        $"only {drawable} of {all.Count} looks are ones we can draw, and the " +
                        "humanoid rig alone is 16% — the rest should be reachable");
        }
    }
}
