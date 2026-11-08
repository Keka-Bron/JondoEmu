using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>
    /// The test harness's routes (ControlApiTest.cs) exist only on a server started for it, with
    /// JONDO_TEST_KEY in its environment: anywhere else /api/test/... is no route at all.
    /// </summary>
    public class TestRoutesTests
    {
        [Fact]
        public void Without_a_test_key_the_test_routes_are_not_there()
        {
            Assert.Equal("", ControlApi.TestKey);
            foreach (string route in new[] { "launch", "token", "session", "fight", "cast", "inject", "refresh", "status" })
            {
                var answer = ControlApi.Responder("/api/test/" + route, "POST", "{\"key\":\"\"}");
                Assert.NotNull(answer);
                Assert.Equal(404, answer.Value.Codigo);
            }
        }
    }
}
