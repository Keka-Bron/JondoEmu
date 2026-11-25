using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;
using static Jondo.Unity.Server.Managers.MobSpawnManager;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// The server's Events page: each yearly event on its calendar, held open or held shut, Kwismas
    /// Island's crossing and the event monsters.
    /// </summary>
    [Collection("MapManager")]
    public sealed class EventSettingsTests
    {
        private static DateTime Day(int year, int month, int day) => new(year, month, day, 12, 0, 0);

        [Theory]
        [InlineData(2026, 12, 14, false)]
        [InlineData(2026, 12, 15, true)]
        [InlineData(2026, 12, 31, true)]
        [InlineData(2027, 1, 10, true)]
        [InlineData(2027, 1, 31, true)]
        [InlineData(2027, 2, 1, false)]
        [InlineData(2026, 11, 24, false)]
        public void Kwismas_runs_across_the_new_year(int year, int month, int day, bool on)
            => Assert.Equal(on, SeasonalEvents.OnTheCalendar(SeasonalEvents.Kwismas, Day(year, month, day)));

        /// <summary>Holding one event open opens no other: on 24 November only Al Howin runs.</summary>
        [Fact]
        public void Holding_one_event_open_leaves_the_others_on_their_calendar()
        {
            EventSettings.UseForTests(new EventSettings { Halouine = EventMode.On });
            try
            {
                var day = Day(2026, 11, 24);
                foreach (int ev in SeasonalEvents.Events)
                    Assert.Equal(ev == SeasonalEvents.Halouine, SeasonalEvents.IsOn(ev, day));
            }
            finally { EventSettings.UseForTests(null); }
        }

        [Fact]
        public void The_sea_passage_to_Vulkania_follows_Vulkania()
        {
            Assert.Equal(SeasonalEvents.Vulkania, SeasonalEvents.Canonical(SeasonalEvents.VulkaniaPassage));
            Assert.True(SeasonalEvents.IsEvent(SeasonalEvents.VulkaniaPassage));
            Assert.True(SeasonalEvents.OnTheCalendar(SeasonalEvents.VulkaniaPassage, Day(2026, 7, 1)));
            Assert.False(SeasonalEvents.OnTheCalendar(SeasonalEvents.VulkaniaPassage, Day(2026, 11, 24)));
            Assert.Equal("vulkania", SeasonalEvents.KeyOf(SeasonalEvents.VulkaniaPassage));
        }

        [Fact]
        public void Every_event_is_known_and_has_a_key()
        {
            foreach (int id in SeasonalEvents.Events)
            {
                Assert.True(SeasonalEvents.IsKnown(id));
                Assert.DoesNotMatch("^[0-9]+$", SeasonalEvents.KeyOf(id));
            }
            Assert.Equal(SeasonalEvents.Events.Length, SeasonalEvents.Events.Select(SeasonalEvents.KeyOf).Distinct().Count());
        }

        [Fact]
        public void The_window_shown_is_the_running_one_or_the_next()
        {
            Assert.Equal((Day(2026, 12, 15).Date, Day(2027, 2, 1).Date),
                         SeasonalEvents.WindowAround(SeasonalEvents.Kwismas, Day(2026, 11, 24)));
            Assert.Equal((Day(2026, 12, 15).Date, Day(2027, 2, 1).Date),
                         SeasonalEvents.WindowAround(SeasonalEvents.Kwismas, Day(2027, 1, 10)));
            var halouine = SeasonalEvents.WindowAround(SeasonalEvents.Halouine, Day(2026, 11, 24));
            Assert.Equal(2027, halouine.Start.Year);
        }

        /// <summary>Held open, an event is on out of its days; held shut, off in them; Auto, its calendar again.</summary>
        [Fact]
        public void The_operator_holds_an_event_open_or_shut()
        {
            var november = Day(2026, 11, 24);
            var july = Day(2026, 7, 1);
            var settings = new EventSettings();
            EventSettings.UseForTests(settings);
            try
            {
                Assert.False(SeasonalEvents.IsOn(SeasonalEvents.Kwismas, november));
                settings.Kwismas = EventMode.On;
                Assert.True(SeasonalEvents.IsOn(SeasonalEvents.Kwismas, november));

                settings.SetMode(SeasonalEvents.VulkaniaPassage, EventMode.Off);
                Assert.Equal(EventMode.Off, settings.Vulkania);
                Assert.False(SeasonalEvents.IsOn(SeasonalEvents.Vulkania, july));
                Assert.False(SeasonalEvents.IsOn(SeasonalEvents.VulkaniaPassage, july));

                // Anything that is not an event is not the page's to shut.
                Assert.True(SeasonalEvents.IsOn(703, july));

                settings.Vulkania = EventMode.Auto;
                Assert.True(SeasonalEvents.IsOn(SeasonalEvents.Vulkania, july));
                Assert.Contains("kwismas On", settings.Describe());
            }
            finally { EventSettings.UseForTests(new EventSettings()); }
        }

        [Fact]
        public void The_settings_survive_a_round_trip_through_their_file_format()
        {
            var settings = new EventSettings { Halouine = EventMode.On, Pwak = EventMode.Off, EventMonsterChance = 70 };
            var options = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            string json = JsonSerializer.Serialize(settings, options);
            Assert.Contains("\"On\"", json);
            var back = JsonSerializer.Deserialize<EventSettings>(json, options)!;
            Assert.Equal(EventMode.On, back.Halouine);
            Assert.Equal(EventMode.Off, back.Pwak);
            Assert.Equal(70, back.EventMonsterChance);
            Assert.True(back.EventMonsters);
        }

        // ---- the event monsters ----

        private static MonsterData Monster(int id, params int[] levels)
            => new() { Id = id, Grades = levels.Select(l => new MonsterGrade { Level = l }).ToList() };

        private static readonly Dictionary<int, MonsterData> Bestiary = new()
        {
            [36] = Monster(36, 1, 2, 3, 4, 5),
            [98] = Monster(98, 1, 2, 3, 4, 5),
            [500] = Monster(500, 100, 110, 120),
            [793] = Monster(793, 2, 4, 6, 8, 10),
            [1232] = Monster(1232, 2, 4, 6, 8, 10),
            [2819] = Monster(2819, 110, 120, 130, 140, 150),
        };

        private static MobGroup Group(long id, params (int Monster, int Level)[] members) => new()
        {
            MobId = id,
            Members = members.Select(m => new MobMember { Monster = Bestiary[m.Monster], Level = m.Level }).ToList(),
        };

        private static readonly List<EventGuests.Rule> Rules = new()
        {
            new() { Event = SeasonalEvents.Halouine, Replace = new() { [36] = 793 } },
            new() { Event = SeasonalEvents.Kwismas, Area = 48, Joins = new[] { 2819 } },
        };

        private static int Dress(IEnumerable<MobGroup> groups, int area, EventSettings settings, ISet<int> running,
                                 Func<long, bool>? fighting = null, IReadOnlyList<int>? zone = null)
            => EventGuests.Dress(groups, area, settings, id => Bestiary.GetValueOrDefault(id),
                                 fighting ?? (_ => false), running.Contains, _ => true,
                                 zone == null ? null : () => zone);

        private static void WithRules(Action body, Func<int, int>? dice = null)
        {
            var before = EventGuests.Dice;
            EventGuests.UseForTests(Rules);
            EventGuests.Dice = dice ?? (_ => 0);
            try { body(); }
            finally
            {
                EventGuests.Dice = before;
                EventGuests.UseForTests(null);
            }
        }

        [Fact]
        public void Al_Howin_puts_a_pumpkin_on_a_Gobball_and_takes_it_off_after()
            => WithRules(() =>
            {
                var settings = new EventSettings { EventMonsterChance = 100 };
                var group = Group(1, (98, 3), (36, 5));
                var running = new HashSet<int> { SeasonalEvents.Halouine };

                Assert.Equal(1, Dress(new[] { group }, 1, settings, running));
                Assert.Equal(98, group.Members[0].Monster.Id);
                Assert.Equal(793, group.Members[1].Monster.Id);
                Assert.Equal(4, group.Members[1].Level);           // the grade nearest level 5: 4 (then 6)

                // Loaded again while the event runs: it keeps its guest, no second one.
                Assert.Equal(0, Dress(new[] { group }, 1, settings, running));
                Assert.Equal(793, group.Members[1].Monster.Id);

                running.Clear();
                Assert.Equal(1, Dress(new[] { group }, 1, settings, running));
                Assert.Equal(36, group.Members[1].Monster.Id);
                Assert.Equal(5, group.Members[1].Level);
            });

        [Fact]
        public void Turning_the_monsters_off_sends_the_guests_home()
            => WithRules(() =>
            {
                var settings = new EventSettings { EventMonsterChance = 100 };
                var group = Group(2, (36, 2));
                var running = new HashSet<int> { SeasonalEvents.Halouine };
                Dress(new[] { group }, 1, settings, running);
                Assert.Equal(793, group.Members[0].Monster.Id);

                settings.EventMonsters = false;
                Dress(new[] { group }, 1, settings, running);
                Assert.Equal(36, group.Members[0].Monster.Id);
            });

        [Fact]
        public void Kwismas_joins_only_the_groups_of_its_area()
            => WithRules(() =>
            {
                var settings = new EventSettings { EventMonsterChance = 100 };
                var running = new HashSet<int> { SeasonalEvents.Kwismas };
                var frigost = Group(3, (500, 120), (500, 110));
                var astrub = Group(4, (500, 120));
                Dress(new[] { frigost }, 48, settings, running);
                Dress(new[] { astrub }, 18, settings, running);
                Assert.Equal(2819, frigost.Members[0].Monster.Id);
                Assert.Equal(120, frigost.Members[0].Level);
                Assert.Equal(500, frigost.Members[1].Monster.Id);
                Assert.Equal(500, astrub.Members[0].Monster.Id);
            });

        [Fact]
        public void A_fight_a_room_and_a_composed_group_are_left_alone()
            => WithRules(() =>
            {
                var settings = new EventSettings { EventMonsterChance = 100 };
                var running = new HashSet<int> { SeasonalEvents.Halouine };
                var fighting = Group(5, (36, 1));
                var composed = Group(6, (36, 1));
                composed.Modular = true;
                var led = Group(7, (36, 1));
                led.Leaders = 1;
                Dress(new[] { fighting, composed, led }, 1, settings, running, id => id == 5);
                Assert.All(new[] { fighting, composed, led }, g => Assert.Equal(36, g.Members[0].Monster.Id));
            });

        /// <summary>A group loses the dice once per event: reloading its map is no second throw.</summary>
        [Fact]
        public void Each_group_throws_the_dice_once()
        {
            int throws = 0;
            WithRules(() =>
            {
                var settings = new EventSettings { EventMonsterChance = 50 };
                var running = new HashSet<int> { SeasonalEvents.Halouine };
                var group = Group(8, (36, 1));
                for (int i = 0; i < 5; i++) Dress(new[] { group }, 1, settings, running);
                Assert.Equal(36, group.Members[0].Monster.Id);
                Assert.Equal(1, throws);
            }, n => { throws++; return n - 1; });
        }

        /// <summary>
        /// The world's own groups name the event monsters too (the subareas' lists do): while the
        /// event is shut they give way to a member of their group, and come back when it opens.
        /// </summary>
        [Fact]
        public void An_event_monster_of_the_world_waits_for_its_event()
            => WithRules(() =>
            {
                var settings = new EventSettings { EventMonsterChance = 0 };
                var running = new HashSet<int>();
                var frigost = Group(10, (500, 110), (2819, 130));

                Assert.Equal(1, Dress(new[] { frigost }, 48, settings, running));
                Assert.Equal(500, frigost.Members[1].Monster.Id);
                Assert.Equal(120, frigost.Members[1].Level);        // the stand-in's grade nearest 130

                Assert.Equal(0, Dress(new[] { frigost }, 48, settings, running));

                running.Add(SeasonalEvents.Kwismas);
                Assert.Equal(1, Dress(new[] { frigost }, 48, settings, running));
                Assert.Equal(2819, frigost.Members[1].Monster.Id);
                Assert.Equal(130, frigost.Members[1].Level);

                // The event monsters turned off on the Events page: out again though Kwismas runs.
                settings.EventMonsters = false;
                Dress(new[] { frigost }, 48, settings, running);
                Assert.Equal(500, frigost.Members[1].Monster.Id);
            });

        /// <summary>A group of event monsters alone takes the monster each is the event's version of.</summary>
        [Fact]
        public void A_group_of_event_monsters_alone_takes_their_ordinary_selves()
            => WithRules(() =>
            {
                var group = Group(11, (793, 4));
                Dress(new[] { group }, 1, new EventSettings(), new HashSet<int>());
                Assert.Equal(36, group.Members[0].Monster.Id);
                Assert.Equal(4, group.Members[0].Level);

                // One with no ordinary self and nobody else in the group: a monster of the map's zone.
                var lone = Group(12, (2819, 120));
                Dress(new[] { lone }, 48, new EventSettings(), new HashSet<int>(), zone: new[] { 2819, 500 });
                Assert.Equal(500, lone.Members[0].Monster.Id);
                Assert.Equal(120, lone.Members[0].Level);

                // And with no zone to draw from, it is left as it is.
                var bare = Group(13, (2819, 120));
                Dress(new[] { bare }, 48, new EventSettings(), new HashSet<int>());
                Assert.Equal(2819, bare.Members[0].Monster.Id);
            });

        [Fact]
        public void No_chance_no_guest()
            => WithRules(() =>
            {
                var group = Group(9, (36, 1));
                Dress(new[] { group }, 1, new EventSettings { EventMonsterChance = 0 },
                      new HashSet<int> { SeasonalEvents.Halouine });
                Assert.Equal(36, group.Members[0].Monster.Id);
            });

        /// <summary>The written rules name monsters the world has, and their events are known ones.</summary>
        [Fact]
        public void The_written_event_monsters_exist()
        {
            if (!File.Exists(Paths.ContentFile(EventGuests.AuthoredFile))) return;
            EventGuests.UseForTests(null);
            var rules = EventGuests.Rules;
            Assert.Contains(rules, r => r.Event == SeasonalEvents.Halouine && r.Replace.Count > 0);
            Assert.Contains(rules, r => r.Event == SeasonalEvents.Kwismas && r.Joins.Length > 0);
            Assert.All(rules, r => Assert.True(SeasonalEvents.IsEvent(r.Event)));
            Assert.True(EventGuests.IsGuest(793));
            Assert.True(EventGuests.IsGuest(2819));
            Assert.False(EventGuests.IsGuest(36));

            try { MobSpawnManager.EnsureMonsterData(); } catch { return; }
            if (MobSpawnManager.AllMonsters.Count == 0) return;
            var known = MobSpawnManager.AllMonsters.Select(m => m.Id).ToHashSet();
            var ids = rules.SelectMany(r => r.Replace.Keys.Concat(r.Replace.Values).Concat(r.Joins)).Distinct();
            Assert.All(ids, id => Assert.Contains(id, known));
        }

        /// <summary>
        /// The Chocrosis Gobball is Sain Ballotin's: on its calendar it roams the Gobball fields the
        /// week of 14 February and gives way to a Gobball the rest of the year, while Al Howin held
        /// open by the operator leaves it out.
        /// </summary>
        [Theory]
        [InlineData(2026, 11, 24, false)]
        [InlineData(2026, 10, 25, false)]
        [InlineData(2026, 2, 9, false)]
        [InlineData(2026, 2, 10, true)]
        [InlineData(2026, 2, 14, true)]
        [InlineData(2026, 2, 17, false)]
        [InlineData(2027, 2, 14, true)]
        public void The_Chocrosis_Gobball_roams_only_in_Sain_Ballotins_week(int year, int month, int day, bool shows)
        {
            if (!File.Exists(Paths.ContentFile(EventGuests.AuthoredFile))) return;
            var before = EventGuests.Dice;
            EventGuests.UseForTests(null);
            EventGuests.Dice = _ => 0;
            EventSettings.UseForTests(new EventSettings { Halouine = EventMode.On, EventMonsterChance = 0 });
            try
            {
                Assert.True(EventGuests.IsGuest(1232));
                var now = new DateTime(year, month, day, 12, 0, 0);
                Assert.Equal(shows, SeasonalEvents.IsOn(SeasonalEvents.SainBallotin, now));
                Assert.True(SeasonalEvents.IsOn(SeasonalEvents.Halouine, now));

                var group = Group(20 + day + 100 * month, (36, 3), (1232, 4));
                EventGuests.Dress(new[] { group }, 1, EventSettings.Current, id => Bestiary.GetValueOrDefault(id),
                                  _ => false, criterion => SeasonalEvents.IsOn(criterion, now), _ => true);
                Assert.Equal(shows ? 1232 : 36, group.Members[1].Monster.Id);
            }
            finally
            {
                EventGuests.Dice = before;
                EventGuests.UseForTests(null);
                EventSettings.UseForTests(null);
            }
        }

        /// <summary>
        /// Hector Kwismas's boat at the Madrestam port shows only while Kwismas runs, and the island's
        /// Hector always, so nobody is stranded; the Al Howin dungeon's portal opens with its event.
        /// </summary>
        [Fact]
        public void The_crossings_are_gated_by_their_events()
        {
            if (!File.Exists(Paths.ContentFile("npcs/spawns.json"))) return;
            using var spawns = JsonDocument.Parse(File.ReadAllText(Paths.ContentFile("npcs/spawns.json")));
            var hector = spawns.RootElement.GetProperty("spawns").EnumerateArray()
                .Where(s => s.GetProperty("npc").GetInt32() == 795).ToList();
            var port = hector.Single(s => s.GetProperty("map").GetInt64() == 68420097);
            Assert.Equal("Sc=" + SeasonalEvents.Kwismas, port.GetProperty("criterion").GetString());
            var shore = hector.Single(s => s.GetProperty("map").GetInt64() == 66060293);
            Assert.False(shore.TryGetProperty("criterion", out _));

            using var passages = JsonDocument.Parse(File.ReadAllText(Paths.ContentFile("interactives/teleports.json")));
            var portal = passages.RootElement.GetProperty("passages").EnumerateArray()
                .Single(p => p.GetProperty("element").GetInt32() == 475425);
            Assert.Equal("Sc=" + SeasonalEvents.Halouine, portal.GetProperty("criterion").GetString());
        }
    }
}
