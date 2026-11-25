using System;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Interactives that only answer when used, as the official server answered (packet captures,
    /// 2026-11-21 to 11-24): a message, an emote learned, a roleplay buff put on.
    /// </summary>
    [Collection("MapManager")]
    public sealed class MeasuredInteractiveMessageBatch2Tests
    {
        private static bool Loaded()
        {
            if (MapManager.GetMapInfo(91889155) == null) MapManager.Initialize();
            InteractiveMessages.Load();
            return MapManager.GetMapInfo(91889155) != null;
        }

        /// <summary>«Usar» taught an emote (keo, the shape of 3.6's khi): 28 «Tocar violão», 5 «Medo».</summary>
        [Theory]
        [InlineData(130811393, 488044, 28)]
        [InlineData(91750913, 472519, 5)]
        public void The_object_teaches_its_emote(long map, int element, int emote)
        {
            if (!Loaded()) return;
            var said = InteractiveMessages.Of(map, element);
            Assert.NotNull(said);
            Assert.Equal((0, 184, emote), (said!.Message, said.Skill, said.Emote));
            Assert.Empty(said.Gives);
        }

        /// <summary>538581 of 91751951 puts on the roleplay buff «Desarranjo Sufokiano» (31477).</summary>
        [Fact]
        public void The_object_gives_its_roleplay_buff()
        {
            if (!Loaded()) return;
            Assert.Equal(new[] { (31477, 1) }, InteractiveMessages.Of(91751951, 538581)!.Gives);
        }

        /// <summary>
        /// Madrestam's wrong clock 467764 says «14» «53» whatever the hour, as measured; Amakna's clock
        /// 474229 says Paris's hour.
        /// </summary>
        [Fact]
        public void The_clocks_say_what_they_said()
        {
            if (!Loaded()) return;
            var wrong = InteractiveMessages.Of(87818755, 467764)!;
            Assert.Equal(127, wrong.Message);
            Assert.Equal(new[] { "14", "53" }, InteractiveMessages.Fill(wrong, "Tester", DateTimeOffset.Now));
            Assert.Equal(120, InteractiveMessages.Of(99091973, 474229)!.Message);
        }

        /// <summary>Objects that only speak: their message, as measured.</summary>
        [Theory]
        [InlineData(171442176, 511363, 842)]
        [InlineData(171445763, 511364, 838)]
        [InlineData(171445761, 540802, 465)]
        [InlineData(205520898, 518647, 1200)]
        public void The_object_says_its_message(long map, int element, int message)
        {
            if (!Loaded()) return;
            Assert.Equal(message, InteractiveMessages.Of(map, element)?.Message);
        }
    }
}
