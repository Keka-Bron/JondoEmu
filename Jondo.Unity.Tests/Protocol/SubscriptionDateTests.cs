using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The subscription end date, and the two ways it has now made the same button go dark.
    /// </summary>
    /// <remarks>
    /// If the client cannot read this date it treats the account as having no subscription, and an
    /// account with no subscription gets ONE character slot. That is why "create a character" was
    /// greyed with a single character on the account, offering to sell more slots.
    ///
    /// It has failed twice for two different reasons. First the format: the value ended in Z and
    /// the real server sends a numeric offset. That was fixed and the button stayed dark, because
    /// the YEAR was still 2099:
    ///
    /// <code>
    ///   2026-09-06  from the real capture   1,788,645,600 s   fits
    ///   2099-01-01  ours                    4,070,901,600 s   does not
    ///   the limit of a 32-bit integer       2,147,483,647 s = 19 January 2038
    /// </code>
    ///
    /// The ordinary year-2038 problem. Every subscription date in the captures sits about eight
    /// days ahead, or is the "1970-01-01T00:00Z" sentinel of an account without one; not one comes
    /// near 2038.
    ///
    /// And it has now failed a third time, in the other place the same question is answered. The
    /// launcher tells the client over Thrift, in userInfo_get, whether the account is subscribed
    /// and until when, and that JSON still carried "2035-01-01T00:00:00Z" -- the trailing Z these
    /// tests exist to forbid -- while asserting against a private copy of the game protocol's
    /// expression rather than against the value anything actually sends. So they were green and
    /// wrong at the same time. Both channels now come from Subscription, and Today() reads it.
    ///
    /// These tests are on the VALUE, not on the button, and that is the honest limit of them: that
    /// the year is what darkens it is an inference. What they can guarantee is that this particular
    /// trap does not come back.
    /// </remarks>
    public class SubscriptionDateTests
    {
        private const string Format = Subscription.Format;

        // The real value, not a copy of the expression that produces it. It used to be a copy, and
        // a copy proves nothing: the launcher was answering 2035-01-01T00:00:00Z over Thrift the
        // whole time these tests were green.
        private static string Today() => Subscription.DefaultEndDate();

        [Fact]
        public void It_stays_inside_what_a_32_bit_second_count_can_hold()
        {
            // The bar the 2099 value failed. Asserted against the number rather than against the
            // year, because "before 2038" is the fact and any year is only a proxy for it.
            var parsed = DateTimeOffset.ParseExact(Today(), Format, CultureInfo.InvariantCulture);

            Assert.True(parsed.ToUnixTimeSeconds() < int.MaxValue,
                        $"{parsed:yyyy-MM-dd} son {parsed.ToUnixTimeSeconds()} segundos y no caben en int32");
        }

        [Fact]
        public void And_it_is_still_comfortably_in_the_future()
        {
            // The other half: a date in the past would say the subscription has run out, which
            // darkens the button just as effectively.
            var parsed = DateTimeOffset.ParseExact(Today(), Format, CultureInfo.InvariantCulture);

            // A week, not six months. It used to be a year out and that is now suspected of being
            // the problem itself: the captures sit about eight days ahead and a subscription does
            // not run past twelve months, so "far in the future" is not the virtue it looked like.
            // What this has to catch is a date in the PAST, which expires the account outright.
            Assert.True(parsed > DateTimeOffset.Now.AddDays(7),
                        "la fecha de abono tiene que estar claramente por delante");
        }

        [Fact]
        public void The_shape_is_the_one_the_real_server_sends()
        {
            // 25 characters, numeric offset, no Z. This is the half that was fixed the first time
            // and it is pinned so it cannot regress while attention is on the year.
            string date = Today();

            Assert.Equal(25, date.Length);
            Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$"), date);
            Assert.DoesNotContain("Z", date);
        }

        [Fact]
        public void The_old_value_would_have_failed_this()
        {
            // Kept as a test rather than only as a comment: it is the whole reason the rule exists,
            // and without it the assertions above look arbitrary.
            var old = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.FromHours(2));

            Assert.True(old.ToUnixTimeSeconds() > int.MaxValue);
            Assert.Equal(4_070_901_600L, old.ToUnixTimeSeconds());
        }

        [Fact]
        public void A_new_account_gets_a_year()
        {
            // It was thirty days for one build, picked while the distance was still suspected of
            // darkening the create button. That suspicion is dead -- it was field 1 of mgq -- and
            // a year is what a subscription in this game tops out at.
            Assert.Equal(365, Subscription.Length.TotalDays);

            var parsed = DateTimeOffset.ParseExact(Today(), Format, CultureInfo.InvariantCulture);
            Assert.True(parsed > DateTimeOffset.Now.AddDays(360));
        }

        [Fact]
        public void An_account_with_nothing_stored_still_gets_a_usable_date()
        {
            // The fallback, and the reason it is not a courtesy: this is asked for on the
            // authentication path and on every launch. An empty string would reach the client as a
            // subscription that ended at the epoch, which is worse than one that is too generous.
            string fallback = Subscription.EndDateFor(0);

            Assert.NotEqual("", fallback);
            Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$"), fallback);
            Assert.True(DateTimeOffset.ParseExact(fallback, Format, CultureInfo.InvariantCulture)
                        > DateTimeOffset.Now);
        }

        [Fact]
        public void The_stored_shape_is_the_one_that_travels()
        {
            // The column holds exactly what goes on the wire, so that nothing converts it on the
            // way out and loses the offset. Both ends of that are the same constant.
            Assert.Equal("yyyy-MM-ddTHH:mm:sszzz", Subscription.Format);
            Assert.Equal(25, Subscription.DefaultEndDate().Length);
        }
    }
}
