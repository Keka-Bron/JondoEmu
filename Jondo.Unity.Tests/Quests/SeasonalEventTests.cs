using System;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>The quests' «Sc»: content that is on, and the yearly events on their dates (Dofus pour les noobs, 2026).</summary>
    /// <remarks>Every event on its calendar, whatever the operator holds in config/events.json.</remarks>
    [Collection("MapManager")]
    public sealed class SeasonalEventTests
    {
        public SeasonalEventTests() => EventSettings.UseForTests(new EventSettings());

        [Theory]
        [InlineData(SeasonalEvents.Halouine, 2026, 10, 22, true)]
        [InlineData(SeasonalEvents.Halouine, 2026, 11, 2, true)]
        [InlineData(SeasonalEvents.Halouine, 2026, 11, 3, false)]
        [InlineData(SeasonalEvents.Halouine, 2026, 10, 1, false)]
        [InlineData(SeasonalEvents.SainBallotin, 2026, 2, 10, true)]
        [InlineData(SeasonalEvents.SainBallotin, 2026, 2, 16, true)]
        [InlineData(SeasonalEvents.SainBallotin, 2026, 2, 17, false)]
        [InlineData(SeasonalEvents.Pwak, 2026, 3, 31, true)]
        [InlineData(SeasonalEvents.Pwak, 2026, 4, 27, true)]
        [InlineData(SeasonalEvents.Pwak, 2026, 4, 28, false)]
        [InlineData(SeasonalEvents.Vulkania, 2026, 7, 1, true)]
        [InlineData(SeasonalEvents.Vulkania, 2026, 9, 1, false)]
        [InlineData(SeasonalEvents.Ripate, 2026, 9, 19, true)]
        [InlineData(SeasonalEvents.Ripate, 2026, 10, 1, false)]
        [InlineData(703, 2026, 1, 1, true)]     // Frigost's quests: always on
        [InlineData(842, 2026, 6, 6, true)]
        public void Each_switch_is_on_when_it_should(int criterion, int year, int month, int day, bool on)
            => Assert.Equal(on, SeasonalEvents.IsOn(criterion, new DateTime(year, month, day, 12, 0, 0)));

        /// <summary>An event held open by the test API is on out of its dates, and back to them when let go.</summary>
        [Fact]
        public void An_event_held_open_is_on_out_of_season()
        {
            var november = new DateTime(2026, 11, 16, 12, 0, 0);
            Assert.False(SeasonalEvents.IsOn(SeasonalEvents.Vulkania, november));
            SeasonalEvents.ForcedOn[SeasonalEvents.Vulkania] = true;
            try
            {
                Assert.True(SeasonalEvents.IsOn(SeasonalEvents.Vulkania, november));
                Assert.False(SeasonalEvents.IsOn(SeasonalEvents.Halouine, november));
            }
            finally { SeasonalEvents.ForcedOn.TryRemove(SeasonalEvents.Vulkania, out _); }
            Assert.False(SeasonalEvents.IsOn(SeasonalEvents.Vulkania, november));
        }
    }
}
