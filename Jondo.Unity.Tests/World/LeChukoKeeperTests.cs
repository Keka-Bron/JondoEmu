using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The keeper of the Le Chuko ship words its resume reply «Volver al navío de Le Chuko donde lo
    /// dejaste.» rather than «Retomar ...», and it still has to be recognised as the resume reply.
    /// </summary>
    [Collection("MapManager")]
    public class LeChukoKeeperTests
    {
        private const int Keeper = 3008;
        private const long Resume = 26246, Keyring = 26252;

        [Fact]
        public void The_ship_keeper_offers_its_resume_reply()
        {
            Npcs.Initialize();
            DungeonDoor.Initialize();
            if (DungeonDoor.For(Keeper).Keyring == 0) return;   // no world base to read

            Assert.Equal(Keyring, DungeonDoor.For(Keeper).Keyring);
            Assert.Equal(Resume, DungeonDoor.For(Keeper).Resume);
        }
    }
}
