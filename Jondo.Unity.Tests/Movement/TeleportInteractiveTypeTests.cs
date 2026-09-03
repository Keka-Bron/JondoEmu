using System.Text.Json;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Movement
{
    [Collection("MapManager")]
    public sealed class TeleportInteractiveTypeTests
    {
        [Fact]
        public void Measured_minus_one_type_is_not_replaced_by_generic_zero()
        {
            using var json = JsonDocument.Parse("""{"interactiveType":-1}""");

            Assert.Equal(-1, TeleportManager.ReadInteractiveType(json.RootElement));
        }

        [Fact]
        public void Missing_type_uses_the_generic_fallback()
        {
            using var json = JsonDocument.Parse("{}");

            Assert.Equal(TeleportManager.GenericTeleportType,
                         TeleportManager.ReadInteractiveType(json.RootElement));
        }

        [Fact]
        public void Exit_skill_is_a_valid_teleport_skill()
        {
            Assert.Equal(339, TeleportManager.ExitSkill);
        }
    }
}
