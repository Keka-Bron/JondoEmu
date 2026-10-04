using System;
using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Emotes;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The emote table and the rules the captures show the real server enforcing.
    /// </summary>
    public class EmoteTests
    {
        private static bool Available => File.Exists(Paths.EmotesJson);

        [Theory]
        // Every emote played in Emotes\captura de muchas actitudes, with the animation its khh named.
        [InlineData(1, "AnimEmoteSit")]
        [InlineData(19, "AnimEmoteRest")]
        [InlineData(34, "AnimEmoteHips")]
        [InlineData(58, "AnimEmoteSeiza")]
        [InlineData(15, "AnimEmotePoint")]
        [InlineData(9, "AnimEmoteHi")]
        [InlineData(2, "AnimEmoteBye")]
        [InlineData(65, "AnimEmoteVulcain")]
        [InlineData(105, "AnimEmoteBoxing")]
        [InlineData(167, "AnimEmoteEtendardbonta")]
        [InlineData(56, "AnimEmoteGift")]
        [InlineData(104, "AnimEmoteRobot")]
        [InlineData(43, "AnimEmoteThumbsup")]
        [InlineData(51, "AnimEmoteSuperhero")]
        [InlineData(247, "AnimEmoteSlip20ans")]
        [InlineData(248, "AnimEmoteMeteor")]
        [InlineData(231, "AnimEmoteAnniversary")]
        [InlineData(241, "AnimEmoteGuerrier")]
        [InlineData(8, "AnimEmoteOups")]
        [InlineData(33, "AnimEmoteShit")]
        [InlineData(90, "AnimEmoteHalloween")]
        [InlineData(6, "AnimEmoteWeap")]
        [InlineData(96, "AnimEmoteShield")]
        [InlineData(97, "AnimEmoteGuild")]
        [InlineData(259, "AnimEmoteCassenoisette")]
        [InlineData(266, "AnimEmoteOcreDragon")]
        [InlineData(29, "AnimEmoteJuggle")]
        public void The_animation_is_the_one_the_capture_broadcast(int emote, string animation)
        {
            if (!Available) return;
            Assert.Equal(animation, new EmoteCatalogue().Of(emote)?.Anim);
        }

        [Fact]
        public void The_capture_that_does_nothing_on_a_mount_is_an_emote_that_forbids_it()
        {
            if (!Available) return;

            // Emotes\usar 2 veces emote reunificacion de los dofus: 208 on a mount gets no khh,
            // and after dismounting it plays.
            var reunification = new EmoteCatalogue().Of(208);
            var now = DateTime.UtcNow;
            Assert.Equal(EmoteRefusal.OnMount, EmoteRules.Check(reunification, true, mounted: true, now, DateTime.MinValue));
            Assert.Equal(EmoteRefusal.None, EmoteRules.Check(reunification, true, mounted: false, now, DateTime.MinValue));
        }

        [Fact]
        public void An_emote_too_soon_after_the_last_is_refused_as_the_capture_does()
        {
            if (!Available) return;

            // Five requests go unanswered 1.76 to 2.08 seconds after an emote; every one 2.61
            // seconds or more after is answered.
            var rest = new EmoteCatalogue().Of(19);
            var played = new DateTime(2026, 8, 13, 0, 38, 0, DateTimeKind.Utc);

            Assert.Equal(EmoteRefusal.TooSoon, EmoteRules.Check(rest, true, false, played.AddMilliseconds(1760), played));
            Assert.Equal(EmoteRefusal.TooSoon, EmoteRules.Check(rest, true, false, played.AddMilliseconds(2084), played));
            Assert.Equal(EmoteRefusal.None, EmoteRules.Check(rest, true, false, played.AddMilliseconds(2607), played));
            Assert.Equal(EmoteRefusal.None, EmoteRules.Check(rest, true, false, played.AddMilliseconds(2930), played));
        }

        [Fact]
        public void An_aura_and_an_emote_not_owned_are_refused()
        {
            if (!Available) return;

            // 171 and 206 are auras: eleven captures send khl for them and get no khh.
            var book = new EmoteCatalogue();
            Assert.Equal(EmoteRefusal.Aura, EmoteRules.Check(book.Of(206), true, false, DateTime.UtcNow, DateTime.MinValue));
            Assert.Equal(EmoteRefusal.NotOwned, EmoteRules.Check(book.Of(29), false, false, DateTime.UtcNow, DateTime.MinValue));
            Assert.Equal(EmoteRefusal.Unknown, EmoteRules.Check(book.Of(999999), true, false, DateTime.UtcNow, DateTime.MinValue));
        }

        [Fact]
        public void A_new_character_starts_with_what_the_creation_captures_show()
        {
            Assert.Equal(new[] { 1, 97, 98, 127 }, EmoteRules.Starting);
        }
    }
}
