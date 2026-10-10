using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// A spell unlocked for the first time goes on the bar by itself, and one the player took off
    /// stays off: the bar is saved whole in the first session, so a level-up has to place what it
    /// opens.
    /// </summary>
    public class SpellBarUnlockTests
    {
        private const int Yopuka = 8;

        [Fact]
        public void A_level_up_places_the_spells_it_opens_and_not_the_ones_taken_off()
        {
            if (!SpellTable.IsLoaded) return;
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                var at50 = SpellTable.KnownFor(Yopuka, 50).Select(k => k.SpellId).ToList();
                int slot = 1;
                foreach (int spell in at50) SpellChoices.PutInBar(slot++, spell);
                int takenOff = at50[0];
                SpellChoices.PutInBar(1, 0);
                session.State.SpellBarLevel = 50;

                SpellChoices.PlaceNewlyUnlocked(Yopuka, 100);

                var opened = SpellTable.KnownFor(Yopuka, 100).Select(k => k.SpellId).Except(at50).ToList();
                Assert.NotEmpty(opened);
                Assert.All(opened, spell => Assert.Contains(spell, SpellChoices.Bar.Values));
                Assert.DoesNotContain(takenOff, SpellChoices.Bar.Values);
                Assert.Equal(100, SpellChoices.BarLevel);
            }
        }

        /// <summary>
        /// A bar saved before its level was kept gets every spell it lacks, once: until then the
        /// bar put them back each time it was drawn, so none had been taken off for good.
        /// </summary>
        [Fact]
        public void A_bar_without_a_level_gets_what_it_lacks_once()
        {
            if (!SpellTable.IsLoaded) return;
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                var known = SpellTable.KnownFor(Yopuka, 200).Select(k => k.SpellId).ToList();
                SpellChoices.PutInBar(1, known[0]);

                SpellChoices.PlaceNewlyUnlocked(Yopuka, 200);

                Assert.All(known, spell => Assert.Contains(spell, SpellChoices.Bar.Values));
                Assert.Equal(known.Count, SpellChoices.Bar.Count);
                Assert.DoesNotContain(0, SpellChoices.Bar.Keys);
            }
        }

        /// <summary>
        /// A level taken back down takes the bar's level with it -- .level 1000 then .level 200 left
        /// the Yopuka's at 1000 -- so the next level-up places what it opens.
        /// </summary>
        [Fact]
        public void A_level_taken_down_lowers_the_bar_s_level()
        {
            if (!SpellTable.IsLoaded) return;
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                var at50 = SpellTable.KnownFor(Yopuka, 50).Select(k => k.SpellId).ToList();
                int slot = 1;
                foreach (int spell in at50) SpellChoices.PutInBar(slot++, spell);
                session.State.SpellBarLevel = 1000;

                SpellChoices.PlaceNewlyUnlocked(Yopuka, 50);
                Assert.Equal(50, SpellChoices.BarLevel);
                Assert.Equal(at50.Count, SpellChoices.Bar.Count);

                SpellChoices.PlaceNewlyUnlocked(Yopuka, 100);
                var opened = SpellTable.KnownFor(Yopuka, 100).Select(k => k.SpellId).Except(at50).ToList();
                Assert.NotEmpty(opened);
                Assert.All(opened, spell => Assert.Contains(spell, SpellChoices.Bar.Values));
            }
        }

        /// <summary>An empty bar is filled whole when it is drawn; here it only gets its level.</summary>
        [Fact]
        public void An_empty_bar_only_records_its_level()
        {
            if (!SpellTable.IsLoaded) return;
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                SpellChoices.PlaceNewlyUnlocked(Yopuka, 60);

                Assert.Empty(SpellChoices.Bar);
                Assert.Equal(60, SpellChoices.BarLevel);

                // And a level already given changes nothing.
                SpellChoices.PutInBar(3, 12345);
                SpellChoices.PlaceNewlyUnlocked(Yopuka, 40);
                Assert.Single(SpellChoices.Bar);
            }
        }
    }
}
