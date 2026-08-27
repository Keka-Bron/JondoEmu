using System;
using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.Security
{
    public class StartupFailureTests
    {
        [Fact]
        public void Fatal_startup_log_keeps_the_exception_type_and_message()
        {
            string log = Program.StartupFailure(
                new InvalidOperationException("SQLite Error 8: attempt to write a readonly database"));

            Assert.Contains(nameof(InvalidOperationException), log);
            Assert.Contains("SQLite Error 8", log);
            Assert.Contains("readonly database", log);
        }
    }
}
