using System.Collections.Generic;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// Several elements that hand out one share each of the same item («upTo»): the six opaline
    /// corals of quest 1743, one per coral.
    /// </summary>
    public class InteractiveGiftShareTests
    {
        private static InteractiveMessages.Said Coral() =>
            new(0, 0, 0, 0, 0, 0, System.Array.Empty<int>(), System.Array.Empty<string>(), System.Array.Empty<int>(), 0, 0)
            {
                Gives = new[] { (17954, 1) },
                UpTo = new Dictionary<int, int> { [17954] = 6 },
            };

        [Fact]
        public void A_share_comes_while_fewer_than_the_cap_are_carried()
        {
            Assert.Equal(new[] { (17954, 1) }, InteractiveMessages.HandOut(Coral(), _ => 3));
            Assert.Empty(InteractiveMessages.HandOut(Coral(), _ => 6));
        }

        [Fact]
        public void An_element_already_taken_from_gives_nothing_more()
        {
            Assert.Empty(InteractiveMessages.HandOut(Coral(), _ => 1, alreadyTook: true));
        }

        [Fact]
        public void The_same_element_is_taken_once_per_character()
        {
            Assert.False(InteractiveMessages.Took(-77001, 169084940, 508291));
            Assert.True(InteractiveMessages.Took(-77001, 169084940, 508291));
            Assert.False(InteractiveMessages.Took(-77002, 169084940, 508291));
        }

        [Fact]
        public void A_gift_without_a_cap_still_comes_only_to_whoever_holds_none()
        {
            var urn = Coral() with { UpTo = new Dictionary<int, int>() };
            Assert.Single(InteractiveMessages.HandOut(urn, _ => 0));
            Assert.Empty(InteractiveMessages.HandOut(urn, _ => 1));
        }
    }
}
