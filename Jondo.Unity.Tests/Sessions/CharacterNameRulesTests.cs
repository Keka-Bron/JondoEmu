using System.Collections.Generic;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// What the server refuses on its own when a character is made: a name the client's naming
    /// rule would not let through, and one character more than the slots of that category.
    /// </summary>
    public sealed class CharacterNameRulesTests
    {
        [Theory]
        [InlineData("Lo")]
        [InlineData("Kaloran")]
        [InlineData("Tor-ka")]
        [InlineData("Tan-Ka-dex")]
        [InlineData("Zyxwvutsrqponmlkjihg")]
        public void Names_the_client_accepts_are_accepted(string name)
            => Assert.True(CharacterNames.IsValid(name));

        [Theory]
        [InlineData("")]
        [InlineData("A")]
        [InlineData("kaloran")]
        [InlineData("KAloran")]
        [InlineData("Kal0ran")]
        [InlineData("Kal ran")]
        [InlineData("Ka-")]
        [InlineData("Ka--lo")]
        [InlineData("Ka-lo-mi-sa")]
        [InlineData("Zyxwvutsrqponmlkjihgf")]
        [InlineData("Kaloran\n")]
        [InlineData("Kalóran")]
        public void Names_the_client_refuses_are_refused(string name)
            => Assert.False(CharacterNames.IsValid(name));

        private static DatabaseManager.DbServer Server(int id, int type) => new() { Id = id, Type = type };
        private static DatabaseManager.DbCharacter On(int server) => new() { ServerId = server, Name = "X" };

        [Fact]
        public void Slots_are_counted_per_category_of_server()
        {
            var servers = new List<DatabaseManager.DbServer> { Server(1, 1), Server(291, 1), Server(295, 0), Server(350, 3) };
            var characters = new List<DatabaseManager.DbCharacter> { On(1), On(1), On(291), On(295), On(350), On(350) };

            Assert.Equal(3, CharacterCreationHandler.SlotsTaken(servers, characters, 1));
            Assert.Equal(3, CharacterCreationHandler.SlotsTaken(servers, characters, 291));
            Assert.Equal(1, CharacterCreationHandler.SlotsTaken(servers, characters, 295));
            Assert.Equal(2, CharacterCreationHandler.SlotsTaken(servers, characters, 350));
        }

        [Fact]
        public void A_server_missing_from_the_list_counts_its_own_characters()
        {
            var characters = new List<DatabaseManager.DbCharacter> { On(7), On(7) };
            Assert.Equal(2, CharacterCreationHandler.SlotsTaken(new List<DatabaseManager.DbServer>(), characters, 7));
        }
    }
}
