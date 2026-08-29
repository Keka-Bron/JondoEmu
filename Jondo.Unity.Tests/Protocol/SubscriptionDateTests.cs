using System;
using System.Globalization;
using System.Text.RegularExpressions;
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
    /// These tests are on the VALUE, not on the button, and that is the honest limit of them: that
    /// the year is what darkens it is an inference. What they can guarantee is that this particular
    /// trap does not come back.
    /// </remarks>
    public class SubscriptionDateTests
    {
        private const string Format = "yyyy-MM-ddTHH:mm:sszzz";

        private static string Today() => DateTimeOffset.Now.AddYears(1).ToString(Format);

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

            Assert.True(parsed > DateTimeOffset.Now.AddMonths(6),
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
    }
}
