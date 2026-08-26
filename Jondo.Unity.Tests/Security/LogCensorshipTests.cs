using Jondo.Unity.Launcher.Network;
using Xunit;

namespace Jondo.Unity.Tests.Security
{
    /// <summary>
    /// No password comes back out of the log.
    /// </summary>
    /// <remarks>
    /// The log goes to the console, to logs\emulator_console.log and to the buffer that serves
    /// /api/registro. Sign-in and account-creation passwords used to travel through all three in
    /// the clear.
    /// </remarks>
    public class LogCensorshipTests
    {
        [Fact]
        public void A_password_in_a_body_is_covered()
        {
            string covered = Censura.Cuerpo("{\"usuario\":\"keka\",\"clave\":\"la de verdad\"}");

            Assert.DoesNotContain("la de verdad", covered);
        }

        [Fact]
        public void What_is_worth_reading_survives_the_censor()
        {
            // A censor that eats everything leaves a log that is no use to anybody, which is how
            // it quietly gets turned off again.
            string covered = Censura.Cuerpo("{\"usuario\":\"keka\",\"clave\":\"la de verdad\"}");

            Assert.Contains("usuario", covered);
            Assert.Contains("keka", covered);
        }

        [Fact]
        public void A_session_identifier_is_not_written_whole()
        {
            Assert.DoesNotContain("1a4b8e", Censura.Valor("2f9c1a4b8e"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json at all")]
        [InlineData("{")]
        public void The_censor_survives_anything_that_is_not_a_body(string body)
        {
            // It runs on the logging path, where an exception would take down whatever was being
            // logged — usually an error somebody was trying to diagnose.
            Censura.Cuerpo(body);
        }
    }
}
