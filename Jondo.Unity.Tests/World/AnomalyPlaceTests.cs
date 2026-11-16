using System;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The temporal anomalies' places: where a travel lands, the guardian's group, the exit and the
    /// clocks (measured in the two anomaly captures; content/anomalies/places.json).
    /// </summary>
    [Collection("MapManager")]
    public class AnomalyPlaceTests
    {
        private const int AnomaliesSubArea = 916;

        [Fact]
        public void Every_place_is_in_the_anomalies_subarea_and_its_entry_has_the_exit()
        {
            if (AnomalyPlaces.All.Count == 0) return;   // no content
            Interactives.Initialize();
            Assert.Equal(11, AnomalyPlaces.All.Count);
            foreach (var place in AnomalyPlaces.All)
            {
                if (DatabaseManager.SubAreaOfMap(place.Entry) == 0) return;   // no world data in this base
                Assert.Equal(AnomaliesSubArea, DatabaseManager.SubAreaOfMap(place.Entry));
                Assert.Equal(AnomaliesSubArea, DatabaseManager.SubAreaOfMap(place.Room));
                var exit = Interactives.ElementsOf(place.Entry).FirstOrDefault(e => e.Id == place.Exit);
                Assert.True(exit.Id != 0, $"{place.Name}: no element {place.Exit} on {place.Entry}");
                Assert.InRange(exit.Gfx, 64415, 64424);
            }
        }

        [Fact]
        public void Noximilian_waits_in_his_workshop_with_his_three_and_fights_on_its_arena()
        {
            var workshop = AnomalyPlaces.ByRoom(196086786);
            if (workshop == null) return;
            Assert.Equal(5781, workshop.Guardian);
            Assert.Equal(new[] { 5781, 5714, 5720, 5710 }, AnomalyPlaces.GroupOf(workshop, new Random(1)));
            Assert.Equal(196089346, AnomalyPlaces.ArenaOf(196086786));
            Assert.Same(workshop, AnomalyPlaces.ByEntry(196085762));
        }

        [Fact]
        public void A_guardian_with_no_measured_group_takes_one_of_each_family()
        {
            var tower = AnomalyPlaces.All.FirstOrDefault(p => p.Guardian == 5684);   // Agonie, Mineral Tower
            if (tower == null) return;
            var group = AnomalyPlaces.GroupOf(tower, new Random(7));
            Assert.Equal(4, group.Count);
            Assert.Equal(5684, group[0]);
            Assert.InRange(group[1], 5714, 5718);   // a brutoforme
            Assert.InRange(group[2], 5719, 5723);   // a cublaforme
            Assert.InRange(group[3], 5709, 5713);   // a kaxaforme
        }

        /// <summary>Julith brings a fourth chronomorph: two of one family.</summary>
        [Fact]
        public void Julith_comes_with_four_chronomorphs()
        {
            var brakmar = AnomalyPlaces.All.FirstOrDefault(p => p.Guardian == AnomalyPlaces.Julith);
            if (brakmar == null) return;
            var group = AnomalyPlaces.GroupOf(brakmar, new Random(7));
            Assert.Equal(5, group.Count);
            Assert.Equal(AnomalyPlaces.Julith, group[0]);
            Assert.All(group.Skip(1), m => Assert.True(AnomalyPlaces.IsChronomorph(m)));
        }

        /// <summary>A place's group is the same through the anomaly's cycle: the same dice, the same three.</summary>
        [Fact]
        public void A_guardians_group_stays_the_same_through_the_cycle()
        {
            var tower = AnomalyPlaces.All.FirstOrDefault(p => p.Guardian == 5684);
            if (tower == null || Anomalies.Duration <= 0) return;
            var now = DateTimeOffset.FromUnixTimeSeconds(1_786_573_500);
            var first = AnomalyPlaces.GroupOf(tower, new Random(AnomalyPlaces.GroupSeed(tower, now)));
            Assert.Equal(first, AnomalyPlaces.GroupOf(tower, new Random(AnomalyPlaces.GroupSeed(tower, now.AddSeconds(30)))));
        }

        /// <summary>
        /// The anomaly fragment's rates (Dofus pour les noobs): 40 % off a chronomorph in the
        /// guardian's room, nothing off the guardian; 1, 5, 10 and 20 % in an anomaly's zone by the
        /// side's elixir; nothing elsewhere. The elixir rows are named by state or alteration.
        /// </summary>
        [Fact]
        public void The_fragments_drop_at_the_guides_rates()
        {
            if (AnomalyPlaces.All.Count == 0) return;
            Assert.Equal(40, AnomalyLoot.FragmentChance(5714, inRoom: true, inZone: false, elixir: 0));
            Assert.Equal(0, AnomalyLoot.FragmentChance(5663, inRoom: true, inZone: false, elixir: 3));
            Assert.Equal(new double[] { 1, 5, 10, 20 },
                Enumerable.Range(0, 4).Select(e => AnomalyLoot.FragmentChance(102, false, true, e)));
            Assert.Equal(0, AnomalyLoot.FragmentChance(102, false, false, 3));

            Assert.Equal(1, AnomalyLoot.ElixirNamed("HS", 3383));
            Assert.Equal(3, AnomalyLoot.ElixirNamed("HA", 52));
            Assert.Null(AnomalyLoot.ElixirNamed("HS", 3382));
            Assert.Null(AnomalyLoot.ElixirNamed("PL", 50));
        }

        [Fact]
        public void An_anomaly_keeps_its_place_through_its_cycle()
        {
            if (AnomalyPlaces.All.Count == 0 || Anomalies.Duration <= 0) return;
            var start = DateTimeOffset.FromUnixTimeSeconds(1_786_573_500);
            long cycle = AnomalyPlaces.Cycle(609, start);
            var place = AnomalyPlaces.Of(609, start);
            Assert.NotNull(place);
            for (int minute = 0; minute < Anomalies.Duration; minute++)
            {
                var then = start.AddMinutes(minute);
                if (AnomalyPlaces.Cycle(609, then) != cycle) break;
                Assert.Same(place, AnomalyPlaces.Of(609, then));
            }
        }

        /// <summary>The capture read the clocks at 22:26 UTC on 12 August: «00», «26» and «4», «26».</summary>
        [Fact]
        public void The_clocks_say_what_the_capture_read()
        {
            if (AnomalyPlaces.WrongBy == 0) return;
            var then = new DateTimeOffset(2026, 8, 12, 22, 26, 13, TimeSpan.Zero);
            Assert.Equal((120, "00", "26"), AnomalyHandler.ClockReading(false, then));
            Assert.Equal((127, "4", "26"), AnomalyHandler.ClockReading(true, then));
        }
    }
}
