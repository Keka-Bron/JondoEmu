using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Koliseo ladder: the client's leagues and their buffer, the rating moved by each fight,
    /// placement, the day and the season, the lty the window draws, and a matchmaking that goes
    /// by rating and keeps levels apart unless the ladder says two players are even.
    /// </summary>
    [Collection("koliseo")]
    public class KoliseoLadderTests : IDisposable
    {
        private static readonly DateTime CapturedSeason = new DateTime(2025, 3, 14, 20, 46, 31, 598, DateTimeKind.Utc);
        private readonly List<long> _characters = new();
        private readonly int _seasons;

        public KoliseoLadderTests()
        {
            KoliseoQueue.ForgetEverything();
            _seasons = KoliseoLadder.LastSeasonNumber();
        }

        public void Dispose()
        {
            foreach (long id in _characters) KoliseoLadder.Erase(id);
            KoliseoLadder.EraseSeasonsAfter(_seasons);
            KoliseoLadder.Clock = () => DateTime.UtcNow;
            KoliseoQueue.ForgetEverything();
        }

        private long Character(long id)
        {
            KoliseoLadder.Erase(id);
            _characters.Add(id);
            return id;
        }

        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        // ─── The leagues ────────────────────────────────────────────────────────────────────

        [Fact]
        public void The_leagues_are_the_clients_twenty_six()
        {
            var all = KoliseoLeagues.All;
            Assert.Equal(26, all.Count);
            Assert.Equal(new[] { "Bronze", "Silver", "Gold", "Platinum", "Diamond" },
                         all.Where(l => !l.Last).Select(l => l.Tier).Distinct());
            Assert.All(all.Where(l => !l.Last).GroupBy(l => l.Tier), tier => Assert.Equal(5, tier.Count()));
            Assert.Equal((6, 0, 419), (all[0].Id, all[0].Low, all[0].High));
            Assert.Equal((47, "Legend", true), (all[^1].Id, all[^1].Tier, all[^1].Last));
            // Each division overlaps the next by 50: the buffer.
            for (int i = 0; i + 2 < all.Count; i++) Assert.Equal(50, all[i].High - all[i + 1].Low + 1);
        }

        [Fact]
        public void Placement_takes_the_first_division_whose_top_the_rating_does_not_pass()
        {
            Assert.Equal(6, KoliseoLeagues.Place(0));
            Assert.Equal(6, KoliseoLeagues.Place(419));
            Assert.Equal(7, KoliseoLeagues.Place(420));
            Assert.Equal(46, KoliseoLeagues.Place(4000));
            Assert.Equal(47, KoliseoLeagues.Place(4001));
            Assert.Equal(47, KoliseoLeagues.Place(9000));
        }

        [Fact]
        public void A_division_is_kept_inside_its_bounds_and_left_outside_them()
        {
            Assert.Equal(7, KoliseoLeagues.Move(7, 400));     // Bronze 2 down to 400: still Bronze 2
            Assert.Equal(6, KoliseoLeagues.Move(6, 400));     // Bronze 1 up to 400: still Bronze 1
            Assert.Equal(7, KoliseoLeagues.Move(6, 420));     // over 419: Bronze 2
            Assert.Equal(6, KoliseoLeagues.Move(7, 369));     // under 370: Bronze 1
            Assert.Equal(8, KoliseoLeagues.Move(6, 700));     // a long way up: where 700 is
            Assert.Equal(47, KoliseoLeagues.Move(46, 4500));
            Assert.Equal(7, KoliseoLeagues.Move(KoliseoLeagues.None, 500));
        }

        // ─── The rating ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Placement_is_five_fights_then_a_league_and_an_even_fight_is_worth_45()
        {
            KoliseoLadder.Clock = () => new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
            long a = Character(9_100_000_001), b = Character(9_100_000_002);
            Assert.Equal(1000, KoliseoLadder.Of(a, 0, 200).Rating);
            Assert.Equal(500, KoliseoLadder.StartingRating(100));

            // Five placement fights, won by a against b: 90 for the first even one, and no
            // league until the fifth.
            var after = KoliseoLadder.Record(0, new[] { (a, 200) }, new[] { (b, 200) });
            Assert.Equal(1090, after.Single(s => s.CharacterId == a).Rating);
            Assert.Equal(910, after.Single(s => s.CharacterId == b).Rating);
            for (int i = 0; i < 3; i++) KoliseoLadder.Record(0, new[] { (a, 200) }, new[] { (b, 200) });
            var winner = KoliseoLadder.Of(a, 0, 200);
            Assert.Equal((1, KoliseoLeagues.None), (winner.PlacementLeft, winner.League));
            after = KoliseoLadder.Record(0, new[] { (a, 200) }, new[] { (b, 200) });
            winner = after.Single(s => s.CharacterId == a);
            var loser = after.Single(s => s.CharacterId == b);
            Assert.True(winner.Placed);
            Assert.Equal(KoliseoLeagues.Place(winner.Rating), winner.League);
            Assert.Equal(KoliseoLeagues.Place(loser.Rating), loser.League);
            Assert.Equal((5, 5, 5, 5), (winner.SeasonWins, winner.SeasonFights, winner.DayWins, winner.DayFights));
            Assert.Equal((0, 5), (loser.SeasonWins, loser.SeasonFights));

            // Placed: K = 90 on the Elo curve, 45 for an even fight.
            Assert.Equal(0.5, KoliseoLadder.Expected(1234, 1234));
            Assert.Equal(45, KoliseoLadder.K / 2);
            int rw = winner.Rating, rl = loser.Rating;
            after = KoliseoLadder.Record(0, new[] { (b, 200) }, new[] { (a, 200) });
            int gain = (int)Math.Round(rl + KoliseoLadder.K * (1 - KoliseoLadder.Expected(rl, rw)), MidpointRounding.AwayFromZero);
            int loss = (int)Math.Round(rw - KoliseoLadder.K * KoliseoLadder.Expected(rw, rl), MidpointRounding.AwayFromZero);
            Assert.Equal(gain, after.Single(s => s.CharacterId == b).Rating);
            Assert.Equal(loss, after.Single(s => s.CharacterId == a).Rating);
        }

        [Fact]
        public void The_day_counters_start_again_each_day_and_a_new_season_starts_everything_again()
        {
            var today = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
            KoliseoLadder.Clock = () => today;
            long a = Character(9_100_000_011), b = Character(9_100_000_012);
            KoliseoLadder.Record(1, new[] { (a, 150) }, new[] { (b, 150) });
            Assert.Equal((1, 1), (KoliseoLadder.Of(a, 1, 150).DayWins, KoliseoLadder.Of(a, 1, 150).DayFights));
            // Another mode is another standing.
            Assert.Equal(0, KoliseoLadder.Of(a, 2, 150).SeasonFights);

            KoliseoLadder.Clock = () => today.AddDays(1);
            var tomorrow = KoliseoLadder.Of(a, 1, 150);
            Assert.Equal((0, 0, 1, 1), (tomorrow.DayWins, tomorrow.DayFights, tomorrow.SeasonWins, tomorrow.SeasonFights));

            int season = KoliseoLadder.Current().Number;
            var over = KoliseoLadder.Current().StartUtc + KoliseoLadder.SeasonLength + TimeSpan.FromMinutes(1);
            KoliseoLadder.Clock = () => over;
            Assert.True(KoliseoLadder.Current().Number > season);
            var fresh = KoliseoLadder.Of(a, 1, 150);
            Assert.Equal((750, KoliseoLadder.PlacementFights, 0), (fresh.Rating, fresh.PlacementLeft, fresh.SeasonFights));
        }

        // ─── The lty ────────────────────────────────────────────────────────────────────────

        /// <summary>Unplaced everywhere, in the captured season: the world entry's lty, byte for byte.</summary>
        [Fact]
        public void An_unplaced_character_gets_the_world_entrys_lty()
        {
            KoliseoLadder.StartSeason(CapturedSeason);
            KoliseoLadder.Clock = () => CapturedSeason.AddDays(10);
            long a = Character(9_100_000_021);
            Assert.Equal(Hex(
                "1218323032352d30332d31345432303a34363a33312e3539385a" +
                "1a1a18ffffffffffffffffff012005420b08ffffffffffffffffff01" +
                "1a1c080118ffffffffffffffffff012005420b08ffffffffffffffffff01" +
                "1a1c080218ffffffffffffffffff012005420b08ffffffffffffffffff01" +
                "1a1c080318ffffffffffffffffff012005420b08ffffffffffffffffff01"),
                KoliseoHandler.BuildRanks(a, 200));
        }

        /// <summary>
        /// After a lost 2v2, as in "koliseo completo": that mode with 4 placement fights left, one
        /// fight in the season and in the day, no win.
        /// </summary>
        [Fact]
        public void A_lost_fight_shows_as_the_captures_does()
        {
            KoliseoLadder.StartSeason(CapturedSeason);
            KoliseoLadder.Clock = () => CapturedSeason.AddDays(10);
            long me = Character(9_100_000_031), mate = Character(9_100_000_032);
            long them1 = Character(9_100_000_033), them2 = Character(9_100_000_034);
            KoliseoLadder.Record(1, new[] { (them1, 200), (them2, 200) }, new[] { (me, 200), (mate, 200) });

            var lty = ProtoMessage.Parse(KoliseoHandler.BuildRanks(me, 200));
            var twoVersusTwo = lty.Fields.Where(f => f.FieldNumber == 3).Select(f => ProtoMessage.Parse(f.BytesValue))
                                  .Single(m => m.Fields.Any(f => f.FieldNumber == 1 && f.VarIntValue == 1));
            long Field(int n) => twoVersusTwo.Fields.FirstOrDefault(f => f.FieldNumber == n)?.VarIntValue ?? 0;
            Assert.Equal((4L, 0L, 1L, 0L, 1L), (Field(4), Field(5), Field(6), Field(7), Field(10)));
            Assert.Equal(-1L, Field(3));
        }

        // ─── The matchmaking ────────────────────────────────────────────────────────────────

        private static void Profiles(Dictionary<long, KoliseoQueue.Profile> profiles)
            => KoliseoQueue.Describe = (id, _) => profiles[id];

        [Fact]
        public void Levels_far_apart_do_not_meet_unless_the_ladder_says_they_are_even()
        {
            var low = new KoliseoQueue.Profile(50, 1000, false);
            var high = new KoliseoQueue.Profile(200, 1000, false);
            Assert.False(KoliseoQueue.CanMeet(low, high, KoliseoQueue.RatingWindowMax));
            Assert.True(KoliseoQueue.CanMeet(low, low with { Level = 70 }, 150));
            // Both placed and within 100 points: the ladder says they are even.
            Assert.True(KoliseoQueue.CanMeet(low with { Placed = true }, high with { Placed = true, Rating = 1080 }, 150));
            Assert.False(KoliseoQueue.CanMeet(low with { Placed = true }, high with { Placed = true, Rating = 1150 }, KoliseoQueue.RatingWindowMax));
            // Nor does waiting widen the level gap: only the rating window grows.
            Assert.Equal(150, KoliseoQueue.RatingWindow(TimeSpan.Zero));
            Assert.Equal(450, KoliseoQueue.RatingWindow(TimeSpan.FromSeconds(30)));
            Assert.Equal(KoliseoQueue.RatingWindowMax, KoliseoQueue.RatingWindow(TimeSpan.FromHours(1)));
        }

        [Fact]
        public void The_rating_window_widens_with_the_wait()
        {
            var now = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
            KoliseoQueue.Clock = () => now;
            Profiles(new Dictionary<long, KoliseoQueue.Profile>
            {
                [1] = new(200, 1000, true),
                [2] = new(200, 1400, true),
            });
            KoliseoQueue.Enrol(1, 0);
            KoliseoQueue.Enrol(2, 0);
            Assert.Null(KoliseoQueue.TryMatch(0, 1));             // 400 apart, a window of 150

            now = now.AddSeconds(30);                             // 450 now
            var match = KoliseoQueue.TryMatch(0, 1);
            Assert.NotNull(match);
            Assert.Equal(new long[] { 1 }, match!.Value.Blue);
            Assert.Equal(new long[] { 2 }, match.Value.Red);
            Assert.Equal(0, KoliseoQueue.Count);
        }

        [Fact]
        public void The_closest_ratings_are_matched_and_the_sides_evened()
        {
            KoliseoQueue.Clock = () => new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
            Profiles(new Dictionary<long, KoliseoQueue.Profile>
            {
                [1] = new(200, 1000, true),
                [2] = new(200, 3000, true),    // far from everybody
                [3] = new(200, 1100, true),
                [4] = new(200, 1050, true),
                [5] = new(200, 950, true),
            });
            foreach (long id in new long[] { 1, 2, 3, 4, 5 }) KoliseoQueue.Enrol(id, 1);

            var match = KoliseoQueue.TryMatch(1, 2);
            Assert.NotNull(match);
            var everyone = match!.Value.Blue.Concat(match.Value.Red).OrderBy(i => i).ToArray();
            Assert.Equal(new long[] { 1, 3, 4, 5 }, everyone);
            Assert.True(KoliseoQueue.Waits(2));
            // 1000+1050 against 1100+950: 2050 each.
            Assert.Equal(new long[] { 1, 4 }, match.Value.Blue.OrderBy(i => i).ToArray());
        }

        [Fact]
        public void A_party_is_never_split_and_level_gaps_keep_players_apart()
        {
            KoliseoQueue.Clock = () => new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
            Profiles(new Dictionary<long, KoliseoQueue.Profile>
            {
                [1] = new(200, 1000, false),
                [2] = new(60, 300, false),     // a party of a 200 and a 60: they chose each other
                [3] = new(200, 1000, false),
                [4] = new(190, 1000, false),
                [5] = new(80, 400, false),
            });
            Assert.Equal(2, KoliseoQueue.EnrolUnit(new long[] { 1, 2 }, 1));
            KoliseoQueue.Enrol(3, 1);
            KoliseoQueue.Enrol(4, 1);
            // The 60 cannot meet the 200s on the other side: no match.
            Assert.Null(KoliseoQueue.TryMatch(1, 2));

            // Nor can two solos of 200 and 80 make a side.
            KoliseoQueue.Leave(1);
            KoliseoQueue.Leave(2);
            KoliseoQueue.Enrol(5, 1);
            Assert.Null(KoliseoQueue.TryMatch(1, 2));
            Assert.Equal(3, KoliseoQueue.Count);
        }

        // ─── Leaving the queue ──────────────────────────────────────────────────────────────

        [Fact]
        public void A_party_that_enrolled_together_leaves_together()
        {
            KoliseoQueue.Clock = () => new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
            Profiles(new Dictionary<long, KoliseoQueue.Profile>
            {
                [1] = new(200, 1000, false),
                [2] = new(200, 1000, false),
                [3] = new(200, 1000, false),
            });
            KoliseoQueue.EnrolUnit(new long[] { 1, 2 }, 1);
            KoliseoQueue.Enrol(3, 1);

            var (mode, members) = KoliseoQueue.LeaveWithUnit(2);
            Assert.Equal(1, mode);
            Assert.Equal(new long[] { 1, 2 }, members.OrderBy(i => i).ToArray());
            Assert.False(KoliseoQueue.Waits(1));
            Assert.True(KoliseoQueue.Waits(3));

            Assert.Equal(-1, KoliseoQueue.LeaveWithUnit(99).Mode);
        }

        /// <summary>
        /// The window's leave button (lsi) takes the party out and tells each of them with the lsx
        /// of leaving: f1 absent -- not searching -- and reason 3, which the client's window reads
        /// as "search a fight" again. For the 2v2 it is the capture's "18032001" byte for byte.
        /// </summary>
        [Fact]
        public async Task The_leave_button_puts_every_window_of_the_party_back_to_search()
        {
            const long leader = 8_950_000_201, partner = 8_950_000_202;
            Profiles(new Dictionary<long, KoliseoQueue.Profile>
            {
                [leader] = new(200, 1000, false),
                [partner] = new(200, 1000, false),
            });
            await using var mine = await PortalTests.Wire.Open(leader);
            await using var theirs = await PortalTests.Wire.Open(partner);
            KoliseoQueue.EnrolUnit(new[] { leader, partner }, 1);

            using (SessionContext.Push(mine.Session))
                await KoliseoHandler.LeaveQueueAsync(null!);

            foreach (var wire in new[] { mine, theirs })
            {
                var (op, payload) = Assert.Single(await wire.Drain());
                Assert.Equal(Op.Lsx, op);
                Assert.Equal(Hex("18032001"), payload);
            }
            Assert.False(KoliseoQueue.Waits(leader));
            Assert.False(KoliseoQueue.Waits(partner));
        }
    }
}
