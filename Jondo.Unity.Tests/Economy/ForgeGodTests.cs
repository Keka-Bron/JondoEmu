using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;
using Effect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Forgegod mode: administrators only, and at the forge every rune goes in clean, with no cap.
    /// </summary>
    [Collection("forgemagic")]
    public class ForgeGodTests
    {
        private const int Strength = 118, Vitality = 125, AP = 111;

        [Theory]
        [InlineData(".forjadios")]
        [InlineData(".forgegod")]
        [InlineData(".forgedieu")]
        public void Only_an_administrator_may(string command)
        {
            Assert.Equal(Roles.Administrador, CommandHandler.RequiredRole(command));
            Assert.False(Roles.AlMenos(Roles.GameMaster, CommandHandler.RequiredRole(command)));
        }

        /// <summary>
        /// What the forge does in forgegod mode is the clean outcome: two AP of exo, and vitality
        /// far past the 101 of weight the odds would refuse, with nothing taken off anything else.
        /// </summary>
        [Fact]
        public void A_clean_rune_knows_no_cap()
        {
            Forgemagic.Declare(Strength, 1);
            Forgemagic.Declare(Vitality, 0.2);
            Forgemagic.Declare(AP, 100, useDice: false);
            var ring = new Forgemagic.Template
            {
                Gid = 900030, Level = 200, Type = 9,
                Lines = new List<Forgemagic.TemplateLine> { new(Strength, 40, 60, 0), new(Vitality, 200, 300, 0) },
            };
            var odds = new Forgemagic.Odds(1, 0, 0);
            var item = new List<Effect> { new(Strength, 60, 0, 0), new(Vitality, 300, 0, 0), new(AP, 1, 0, 0) };

            // The odds refuse both outright.
            Assert.Equal(0, Forgemagic.OddsOf(ring, item, new Forgemagic.Rune(1, AP, 1)).Clean);
            Assert.Equal(0, Forgemagic.OddsOf(ring, new[] { new Effect(Vitality, 600, 0, 0) }, new Forgemagic.Rune(1, Vitality, 50)).Clean);

            var twoAp = Forgemagic.Resolve(ring, item, new Forgemagic.Rune(1, AP, 1), Forgemagic.Outcome.Clean, odds, new System.Random(1));
            Assert.Equal(2, twoAp.Effects.Single(e => e.Effect == AP).Value);
            Assert.Equal(60, twoAp.Effects.Single(e => e.Effect == Strength).Value);
            Assert.Empty(twoAp.Lost);

            var vitality = Forgemagic.Resolve(ring, new[] { new Effect(Vitality, 600, 0, 0) }, new Forgemagic.Rune(1, Vitality, 50),
                                              Forgemagic.Outcome.Clean, odds, new System.Random(1));
            Assert.Equal(650, vitality.Effects.Single(e => e.Effect == Vitality).Value);
        }
    }
}
