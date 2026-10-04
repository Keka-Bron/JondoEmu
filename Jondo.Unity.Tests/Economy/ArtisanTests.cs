using System;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// The artisans' directory against its four captures (Oficios/"abrir interfaz oficios-constar
    /// en la lista publica ...", "dejar de constar en lista artesanos ...", "consultar lista de
    /// artesanos en el interactivo del libro").
    /// </summary>
    [Collection("forgemagic")]
    public class ArtisanTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        /// <summary>The 21 jobs the real isd lists, in its order.</summary>
        private static readonly int[] Jobs = { 1, 2, 11, 13, 15, 16, 24, 26, 27, 28, 36, 41, 44, 48, 60, 62, 63, 64, 65, 74, 79 };

        [Fact]
        public void The_settings_of_every_job_the_way_the_capturer_had_them()
        {
            var state = new Jondo.Unity.Server.SessionState();
            state.CrafterSettings[2] = new ArtisanHandler.Setting(10, true, false);
            state.CrafterSettings[11] = new ArtisanHandler.Setting(30, true, false);
            state.CrafterSettings[16] = new ArtisanHandler.Setting(100, true, false);
            state.CrafterSettings[24] = new ArtisanHandler.Setting(150, true, false);
            state.CrafterSettings[27] = new ArtisanHandler.Setting(100, true, false);
            state.CrafterSettings[28] = new ArtisanHandler.Setting(69, false, false);
            state.CrafterSettings[36] = new ArtisanHandler.Setting(100, true, false);
            state.CrafterSettings[60] = new ArtisanHandler.Setting(150, true, false);

            Assert.Equal(Hex("0a04180120010a0618022001280a0a06180b2001281e0a06180d200128010a06180f200128010a0618102001" +
                             "28640a07181820012896010a06181a200128010a06181b200128640a04181c28450a061824200128640a0618" +
                             "29200128010a06182c200128010a061830200128010a07183c20012896010a06183e200128010a06183f2001" +
                             "28010a061840200128010a061841200128010a06184a200128010a06184f20012801"),
                         ArtisanHandler.BuildSettings(state, Jobs));
        }

        [Fact]
        public void Listing_the_farmer_and_the_book_of_his_workshop()
        {
            Assert.Equal(Hex("0a04081c1001"), ArtisanHandler.BuildListing(new[] { (28, true) }));
            Assert.Equal(Hex("0a02081c"), ArtisanHandler.BuildListing(new[] { (28, false) }));
            Assert.Equal(Hex("12011c"), ArtisanHandler.BuildBook(new[] { 28 }));
            // The magi's book in Bonta: the six magus jobs, packed.
            Assert.Equal(Hex("1206" + "30404a2c3e3f"), ArtisanHandler.BuildBook(new[] { 48, 64, 74, 44, 62, 63 }));
        }

        /// <summary>A level-20 farmer as the list shows him: minimum level 1, free, on map 212600322.</summary>
        [Fact]
        public void An_artisan_in_the_list()
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = 901880676642;
            session.State.CharacterName = "Geno-Spear";
            session.State.Breed = 20;
            session.State.MapId = 212600322;
            session.State.Jobs[28] = new JobExperience.Progress { JobId = 28, Experience = JobExperience.Floor(20) };

            Assert.Equal(Hex("0a08101c1801200128141220" + "0a020801120a47656e6f2d5370656172181420a2829ce29f1a3a0508828cb065"),
                         ArtisanHandler.Entry(session, 28).Build());
        }
    }
}
