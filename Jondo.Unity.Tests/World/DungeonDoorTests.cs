using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// What the dungeon door tells the player when it does not open.
    /// </summary>
    /// <remarks>
    /// A player stood at the Granero del Girasol Hambriento (dungeon 8, entrance 192937992, needs
    /// item 8143 or the keyring 10207), answered the guardian, and nothing happened. The server had
    /// worked it out correctly and written the answer to its own console --
    /// "[Mazmorra] Granero del Girasol Hambriento: falta la llave (8143 x1, o el manojo 10207) y no
    /// hay manojo" -- and sent the client nothing at all. Silence is not a refusal: from the
    /// player's chair it is indistinguishable from the feature not existing.
    ///
    /// These tests pin the two sentences by their TEXT rather than by their number, because the
    /// number is only ever wrong in a way that still compiles.
    /// </remarks>
    public class DungeonDoorTests
    {
        public DungeonDoorTests() => InfoMessages.Initialize();

        private static bool Loaded => InfoMessages.Count > 0;

        [Fact]
        public void The_missing_key_warning_says_the_item_is_missing()
        {
            if (!Loaded) return;   // datos/mensajes_*.json is not on this machine

            Assert.Equal("No tienes el objeto necesario.",
                         InfoMessages.Text(InfoMessages.Warning, InfoMessages.MissingItem));
        }

        [Fact]
        public void The_level_warning_is_about_the_character_and_not_a_profession()
        {
            if (!Loaded) return;

            // The bug this replaces, spelled out: 284 is a real message and a real sentence, and it
            // is about a PROFESSION. Turning a level 8 player away from a level 10 door with it
            // sends them off to level a trade that has nothing to do with the door.
            Assert.Equal("No tienes el nivel requerido.",
                         InfoMessages.Text(InfoMessages.Warning, InfoMessages.LevelTooLow));

            Assert.Equal("No tienes el nivel de oficio necesario.",
                         InfoMessages.Text(InfoMessages.Warning, InfoMessages.JobLevelTooLow));

            Assert.NotEqual(InfoMessages.LevelTooLow, InfoMessages.JobLevelTooLow);
        }

        [Fact]
        public void Both_warnings_exist_in_the_clients_own_table()
        {
            if (!Loaded) return;

            // The reason to ask: a message id the client does not know draws nothing, which puts us
            // straight back to the silent door this came from.
            Assert.True(InfoMessages.Exists(InfoMessages.Warning, InfoMessages.MissingItem));
            Assert.True(InfoMessages.Exists(InfoMessages.Warning, InfoMessages.LevelTooLow));
        }
    }
}
