using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.Commands
{
    /// <summary>".sueno": who may write it, under its four names, and the row it reads.</summary>
    public class DreamCommandTests
    {
        /// <summary>It skips the game: administrators only, in Spanish, English and French alike.</summary>
        [Theory]
        [InlineData(".sueno")]
        [InlineData(".sueño")]
        [InlineData(".dream")]
        [InlineData(".reve")]
        public void Skipping_a_dream_is_for_administrators(string command)
            => Assert.Equal(Roles.Administrador, CommandHandler.RequiredRole(command));

        /// <summary>Alone it is the end; with a number, that row.</summary>
        [Theory]
        [InlineData("", null)]
        [InlineData("  ", null)]
        [InlineData("25", 25)]
        [InlineData(" 1 ", 1)]
        [InlineData("26", 26)]
        public void The_row(string rest, int? row)
        {
            Assert.True(CommandHandler.TryParseDreamRow(rest, out int? read));
            Assert.Equal(row, read);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-3")]
        [InlineData("final")]
        [InlineData("2 3")]
        public void Anything_else_is_the_usage(string rest)
            => Assert.False(CommandHandler.TryParseDreamRow(rest, out _));
    }
}
