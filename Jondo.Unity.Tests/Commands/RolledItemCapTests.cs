using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Commands
{
    /// <summary>
    /// Giving items rolled, by ".item &lt;id&gt; &lt;quantity&gt; random" or by the control API: each one
    /// is a row and a message to the client, so a hundred at most. What rolls nothing joins one stack.
    /// </summary>
    [Collection("forgemagic")]
    public class RolledItemCapTests
    {
        private const int Rolls = 990_001, Fixed = 990_002;

        public RolledItemCapTests()
        {
            Forgemagic.Declare(new Forgemagic.Template
            {
                Gid = Rolls, Level = 200, Type = 1,
                Lines = new[] { new Forgemagic.TemplateLine(118, 40, 60, 0) },
            });
            Forgemagic.Declare(new Forgemagic.Template
            {
                Gid = Fixed, Level = 1, Type = 15,
                Lines = new[] { new Forgemagic.TemplateLine(118, 5, 5, 0) },
            });
        }

        [Fact]
        public void A_hundred_items_that_roll_at_most()
        {
            Assert.False(CommandHandler.TooManyRolled(Rolls, CommandHandler.MaxRolledItems));
            Assert.True(CommandHandler.TooManyRolled(Rolls, CommandHandler.MaxRolledItems + 1));
            Assert.True(CommandHandler.TooManyRolled(Rolls, 1_000_000));
        }

        [Fact]
        public void What_rolls_nothing_stacks_and_has_no_cap()
            => Assert.False(CommandHandler.TooManyRolled(Fixed, 1_000_000));
    }
}
