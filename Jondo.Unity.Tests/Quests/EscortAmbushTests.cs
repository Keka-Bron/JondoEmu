using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// «Get Your Fresh Snapper Here!» (579): the domesticated pingwobble walks with the character for
    /// thirty minutes from the moment he is given it, and the monsters waiting at [-64,-51] attack as
    /// soon as he comes onto the map with it, once.
    /// </summary>
    public sealed class EscortAmbushTests
    {
        private const long Map = 54_000_001;
        private const int Mark = 990_579;

        private static NpcFights.Fight Row(string json) => NpcFights.Parse(JsonDocument.Parse(json).RootElement);

        [Fact]
        public void An_enter_row_waits_on_its_map_until_it_is_won()
        {
            var ambush = Row($$"""{"map":{{Map}},"enter":true,"criterion":"PO=11265","marks":{{Mark}},"monsters":[{"monster":2853,"grade":1}]}""");
            var talk = Row($$"""{"npc":1297,"map":{{Map}},"reply":1,"monsters":[{"monster":2853,"grade":1}]}""");
            var rows = new[] { talk, ambush };
            Assert.True(ambush.OnEnter);
            Assert.False(talk.OnEnter);

            Assert.Same(ambush, NpcFights.EntryAmong(rows, Map, _ => false, _ => true));
            // Another map, the criterion unmet (no pingwobble with him), or the ambush already won.
            Assert.Null(NpcFights.EntryAmong(rows, Map + 1, _ => false, _ => true));
            Assert.Null(NpcFights.EntryAmong(rows, Map, _ => false, _ => false));
            Assert.Null(NpcFights.EntryAmong(rows, Map, m => m == Mark, _ => true));
        }

        [Fact]
        public void A_follower_lasts_its_minutes_from_its_receipt()
        {
            const long given = 1_790_000_000;
            var received = new[] { new Equipment.ItemEffect(Equipment.ReceivedOn, given, 0, 0) };
            Assert.False(FollowerLifetimes.Ended(30, received, given + 29 * 60));
            Assert.True(FollowerLifetimes.Ended(30, received, given + 30 * 60));
            // No limit, or no date to count from.
            Assert.False(FollowerLifetimes.Ended(0, received, given + 3600));
            Assert.False(FollowerLifetimes.Ended(30, Enumerable.Empty<Equipment.ItemEffect>(), given + 3600));
        }

        [Fact]
        public void The_pingwobble_lasts_thirty_minutes()
        {
            if (!System.IO.File.Exists(Jondo.Unity.Launcher.Paths.ContentFile(FollowerLifetimes.AuthoredFile))) return;
            FollowerLifetimes.Forget();
            Assert.Equal(30, FollowerLifetimes.MinutesOf(11265));
            Assert.Equal(0, FollowerLifetimes.MinutesOf(1));
        }
    }
}
