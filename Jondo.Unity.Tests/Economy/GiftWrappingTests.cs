using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// The gift-wrapping table: a paper names the present it makes in its effect 1083.
    /// </summary>
    [Collection("forgemagic")]
    public class GiftWrappingTests
    {
        [Fact]
        public void A_paper_names_its_present_and_anything_else_names_none()
        {
            Forgemagic.Declare(new Forgemagic.Template
            {
                Gid = 990_101, Level = 1, Type = 48,
                Lines = new[] { new Forgemagic.TemplateLine(GiftWrapping.WrapsInto, 0, 0, 990_102) },
            });
            Forgemagic.Declare(new Forgemagic.Template
            {
                Gid = 990_103, Level = 1, Type = 48,
                Lines = new[] { new Forgemagic.TemplateLine(118, 1, 5, 0) },
            });

            Assert.Equal(990_102, GiftWrapping.PresentOf(990_101));
            Assert.Equal(0, GiftWrapping.PresentOf(990_103));
        }

        [Fact]
        public void The_birthday_paper_makes_the_birthday_present()
        {
            if (!File.Exists(Paths.WorldDb)) return;
            Assert.Equal(13142, GiftWrapping.PresentOf(12900));
        }
    }
}
