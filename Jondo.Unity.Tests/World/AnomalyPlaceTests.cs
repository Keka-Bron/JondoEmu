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
            var brakmar = AnomalyPlaces.All.FirstOrDefault(p => p.Guardian == 5663);   // Julith
            if (brakmar == null) return;
            var group = AnomalyPlaces.GroupOf(brakmar, new Random(7));
            Assert.Equal(4, group.Count);
            Assert.Equal(5663, group[0]);
            Assert.InRange(group[1], 5714, 5718);   // a brutoforme
            Assert.InRange(group[2], 5719, 5723);   // a cublaforme
            Assert.InRange(group[3], 5709, 5713);   // a kaxaforme
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

        /// <summary>
        /// The world map's anomalies panel: every zone the official server lists (297), each an lzj
        /// { f2 the subarea } -- and an open anomaly's { f1 its zone's 120 %, f2, f3 open, f4 its
        /// closing in milliseconds within the anomaly's two hours }.
        /// </summary>
        [Fact]
        public void The_anomalies_panel_lists_every_zone_and_each_open_anomaly()
        {
            if (Anomalies.All.Count == 0 || AnomalyZones.SubAreas.Count == 0) return;
            Assert.Equal(297, AnomalyZones.SubAreas.Count);
            var now = DateTimeOffset.FromUnixTimeSeconds(1_786_573_500);
            var entries = Jondo.Unity.Server.Network.ProtoMessage.Parse(AnomalyHandler.BuildSubareaInformation(now))
                .Fields.Where(f => f.FieldNumber == 1)
                .Select(f => Jondo.Unity.Server.Network.ProtoMessage.Parse(f.BytesValue).Fields
                    .ToDictionary(x => x.FieldNumber, x => x.VarIntValue))
                .ToList();
            Assert.Equal(AnomalyZones.SubAreas.Union(Anomalies.All.Select(a => a.SubAreaId)).Count(), entries.Count);

            var open = entries.Where(e => e.ContainsKey(3)).ToList();
            Assert.Equal(Anomalies.All.Count, open.Count);
            foreach (var e in open)
            {
                Assert.Equal(AnomalyHandler.AnomalyZoneBonus, e[1]);
                Assert.True(Anomalies.TryGet((int)e[2], out _));
                Assert.InRange(e[4] - now.ToUnixTimeMilliseconds(), 60_000, Anomalies.Duration * 60_000L);
            }
            Assert.All(entries.Where(e => !e.ContainsKey(3)), e => Assert.Equal(new[] { 2 }, e.Keys));
        }

        /// <summary>
        /// An open anomaly's zone gives 120 % more experience and loot chance -- the chance capped at
        /// 100 % --; elsewhere nothing, and the map's widget is told the zone's rate (lvb).
        /// </summary>
        [Fact]
        public void An_anomalys_zone_gives_its_bonus_and_nowhere_else()
        {
            Assert.Equal(2.2, AnomalyHandler.WithZoneBonus(1.0, 120), 6);
            Assert.Equal(100.0, AnomalyHandler.WithZoneBonus(60.0, 120));
            Assert.Equal(1.0, AnomalyHandler.WithZoneBonus(1.0, 0));
            Assert.Equal(220L, AnomalyHandler.WithZoneBonus(100L, 120));
            Assert.Equal(100L, AnomalyHandler.WithZoneBonus(100L, 0));

            if (Anomalies.All.Count == 0) return;
            long zoneMap = DatabaseManager.MapsOfSubArea(Anomalies.All[0].SubAreaId).DefaultIfEmpty(0).Min();
            if (zoneMap == 0) return;
            Assert.Equal(AnomalyHandler.AnomalyZoneBonus, AnomalyHandler.ZoneBonusAt(zoneMap));
            Assert.Equal(0, AnomalyHandler.ZoneBonusAt(191105026));   // Astrub
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
