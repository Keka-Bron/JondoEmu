using System;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The server's settings, as its Settings window saves them: the bonuses, the caps, the raid's
    /// minimum and Hardcore's monsters. At their defaults they leave the game as it is.
    /// </summary>
    [Collection("guild raids")]
    public class ServerSettingsTests
    {
        [Fact]
        public void Bonuses_and_caps_leave_the_game_alone_at_zero()
        {
            Assert.Equal(1000, ServerSettings.WithBonus(1000, 0));
            Assert.Equal(1500, ServerSettings.WithBonus(1000, 50));
            Assert.Equal(3000, ServerSettings.WithBonus(1000, 200));

            Assert.Equal(10.0, ServerSettings.ChanceWithBonus(10.0, 0));
            Assert.Equal(15.0, ServerSettings.ChanceWithBonus(10.0, 50));
            Assert.Equal(100.0, ServerSettings.ChanceWithBonus(80.0, 100));    // never over certainty

            Assert.Equal(14, ServerSettings.Capped(14, 0));
            Assert.Equal(12, ServerSettings.Capped(14, 12));
            Assert.Equal(9, ServerSettings.Capped(9, 12));
        }

        /// <summary>The raid's minimum from the settings, between one and the raid's maximum; 0 is the game's.</summary>
        [Fact]
        public void The_settings_set_the_raid_s_minimum()
        {
            var abyss = GuildRaidCatalogue.Of(Raids.Gigalodon);
            try
            {
                ServerSettings.UseForTests(new ServerSettings());
                Assert.Equal(8, GuildRaidBoard.MinPlayersOf(abyss));
                ServerSettings.UseForTests(new ServerSettings { RaidMinPlayers = 2 });
                Assert.Equal(2, GuildRaidBoard.MinPlayersOf(abyss));
                ServerSettings.UseForTests(new ServerSettings { RaidMinPlayers = 40 });
                Assert.Equal(12, GuildRaidBoard.MinPlayersOf(abyss));
            }
            finally
            {
                ServerSettings.UseForTests(new ServerSettings());
            }
        }

        /// <summary>Hardcore draws every monster at twice its size: scale 200 in the look's f5.</summary>
        [Fact]
        public void Hardcore_monsters_are_twice_as_big()
        {
            var normal = ProtoMessage.Parse(ConnectionProtocol.MonsterLook("{1003}", false).Build()).Fields.ToList();
            var hardcore = ProtoMessage.Parse(ConnectionProtocol.MonsterLook("{1003}", true).Build()).Fields.ToList();
            Assert.DoesNotContain(normal, f => f.FieldNumber == 5);
            var scale = hardcore.Single(f => f.FieldNumber == 5);
            Assert.Equal(new byte[] { 0xC8, 0x01 }, scale.BytesValue);      // packed 200
        }

        /// <summary>Two settings differ only in what changes the game, not in the window's language.</summary>
        [Fact]
        public void The_window_s_language_needs_no_restart()
        {
            var a = new ServerSettings { WindowLanguage = "es" };
            var b = new ServerSettings { WindowLanguage = "fr" };
            Assert.False(a.DiffersFrom(b));
            b.Hardcore = true;
            Assert.True(a.DiffersFrom(b));
        }
    }
}
