using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Auth
{
    /// <summary>
    /// The limit on guessing passwords: one test per hole the old version had.
    /// </summary>
    /// <remarks>
    /// These run one at a time (<see cref="CollectionAttribute"/>) because the throttle is static
    /// state shared by the whole process, which is exactly what it has to be to work, and exactly
    /// what makes parallel tests stamp on each other.
    /// </remarks>
    [Collection("LoginThrottle")]
    public class LoginThrottleTests : IDisposable
    {
        private static readonly DateTime Noon = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

        public LoginThrottleTests() => LoginThrottle.Clear();
        public void Dispose() => LoginThrottle.Clear();

        [Fact]
        public void Five_go_through_and_the_sixth_waits()
        {
            for (int i = 1; i <= LoginThrottle.MaxAttempts; i++)
            {
                Assert.True(LoginThrottle.TryBegin("1.1.1.1", "keka", Noon, out _), $"el intento {i}");
            }

            Assert.False(LoginThrottle.TryBegin("1.1.1.1", "keka", Noon, out string error));
            Assert.Contains("Anti-DDoS", error);
        }

        [Fact]
        public void Counting_happens_before_the_answer_is_known()
        {
            // The bug in one line. The old code read the counter at the top of the login method and
            // wrote it at the bottom, with a PBKDF2 in between, so twenty simultaneous attempts all
            // read zero and all paid for their own hash. Here every call books its attempt before
            // returning, so twenty parallel calls cannot produce more than five yeses.
            var yes = new bool[40];
            Parallel.For(0, 40, i => yes[i] = LoginThrottle.TryBegin("2.2.2.2", "keka", Noon, out _));

            Assert.Equal(LoginThrottle.MaxAttempts, yes.Count(ok => ok));
        }

        [Fact]
        public void A_good_password_does_not_wipe_the_failures_before_it()
        {
            // Whoever holds one working account -- trivial on a public test server, anyone can
            // register -- used to reset the whole limit for their address by logging into it.
            LoginThrottle.TryBegin("3.3.3.3", "victima", Noon, out _);
            LoginThrottle.TryBegin("3.3.3.3", "victima", Noon, out _);
            LoginThrottle.TryBegin("3.3.3.3", "victima", Noon, out _);

            LoginThrottle.TryBegin("3.3.3.3", "lasuya", Noon, out _);
            LoginThrottle.Succeeded("3.3.3.3", "lasuya");

            // Four booked, one given back for the success: three stand. Two left, then the wait.
            Assert.True(LoginThrottle.TryBegin("3.3.3.3", "victima", Noon, out _));
            Assert.True(LoginThrottle.TryBegin("3.3.3.3", "victima", Noon, out _));
            Assert.False(LoginThrottle.TryBegin("3.3.3.3", "victima", Noon, out _));
        }

        [Fact]
        public void Honest_logins_never_add_up()
        {
            // The other half of the same deal: if a success did not give its own attempt back, a
            // family behind one address would lock themselves out by logging in normally.
            for (int i = 0; i < 50; i++)
            {
                Assert.True(LoginThrottle.TryBegin("4.4.4.4", "santi", Noon, out _));
                LoginThrottle.Succeeded("4.4.4.4", "santi");
            }
        }

        [Fact]
        public void One_account_guessed_from_a_thousand_addresses_still_trips()
        {
            // What per-address counting alone never caught, and the cheapest attack to rent.
            for (int i = 1; i <= LoginThrottle.MaxAttempts; i++)
            {
                Assert.True(LoginThrottle.TryBegin($"10.0.0.{i}", "keka", Noon, out _));
            }

            Assert.False(LoginThrottle.TryBegin("10.0.0.99", "keka", Noon, out _));
        }

        [Fact]
        public void A_locked_account_name_does_not_take_its_address_down_with_it()
        {
            // One person hammering "admin" from the office must not lock the office out of their
            // own accounts. The address is charged for the attempt and then refunded, so the five
            // refusals below leave it exactly where it started.
            for (int i = 1; i <= LoginThrottle.MaxAttempts; i++)
            {
                LoginThrottle.TryBegin($"10.1.0.{i}", "admin", Noon, out _);
            }

            for (int i = 0; i < LoginThrottle.MaxAttempts; i++)
            {
                Assert.False(LoginThrottle.TryBegin("5.5.5.5", "admin", Noon, out _));
            }

            Assert.True(LoginThrottle.TryBegin("5.5.5.5", "santi", Noon, out _));
        }

        [Fact]
        public void The_wait_ends_when_the_window_does()
        {
            for (int i = 0; i <= LoginThrottle.MaxAttempts; i++)
            {
                LoginThrottle.TryBegin("6.6.6.6", "keka", Noon, out _);
            }

            Assert.False(LoginThrottle.TryBegin("6.6.6.6", "keka", Noon.AddSeconds(59), out _));
            Assert.True(LoginThrottle.TryBegin("6.6.6.6", "keka", Noon + LoginThrottle.Window, out _));
        }

        [Fact]
        public void Hammering_does_not_push_the_deadline_further_away()
        {
            // Deliberate, and it reads backwards until you think about who is behind the address.
            // If every refusal reset the clock, whoever is hammering could hold everybody sharing
            // their NAT out indefinitely, for free. The clock runs from the last attempt that was
            // actually counted.
            for (int i = 0; i <= LoginThrottle.MaxAttempts; i++)
            {
                LoginThrottle.TryBegin("7.7.7.7", "keka", Noon, out _);
            }

            for (int second = 1; second < 60; second++)
            {
                LoginThrottle.TryBegin("7.7.7.7", "keka", Noon.AddSeconds(second), out _);
            }

            Assert.True(LoginThrottle.TryBegin("7.7.7.7", "keka", Noon.AddSeconds(60), out _));
        }

        [Fact]
        public void Nothing_is_booked_when_there_is_no_name()
        {
            // An empty login is refused before the throttle sees it, so a flood of empty requests
            // cannot lock out the address they came from.
            Assert.True(LoginThrottle.TryBegin("8.8.8.8", "", Noon, out _));
            Assert.Equal(1, LoginThrottle.Watched);
        }

        [Fact]
        public void The_message_says_how_long_and_never_says_a_negative_number()
        {
            for (int i = 0; i <= LoginThrottle.MaxAttempts; i++)
            {
                LoginThrottle.TryBegin("9.9.9.9", "keka", Noon, out _);
            }

            LoginThrottle.TryBegin("9.9.9.9", "keka", Noon.AddSeconds(30), out string error);
            Assert.Contains("30 s", error);

            // A clock that has already run out must not report a negative wait. Bump clamps at
            // zero; without that clamp this line reads "for -3 s", which is the kind of detail
            // that makes a server look broken when it is working.
            LoginThrottle.TryBegin("9.9.9.9", "keka", Noon.AddSeconds(59.999), out string almost);
            Assert.DoesNotContain("for -", almost);
        }
    }
}
