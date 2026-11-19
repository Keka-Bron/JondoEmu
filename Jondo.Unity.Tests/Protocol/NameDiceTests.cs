using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The character creation's dice: asked with kwd, answered with a kvk whose name the client's
    /// own first naming rule accepts.
    /// </summary>
    public sealed class NameDiceTests
    {
        private static readonly Regex NamingRule = new(@"^([A-Z][a-z]+(\-[a-zA-Z][a-z]*){0,2})$");

        [Fact]
        public async Task The_dice_answers_a_name_the_client_takes()
        {
            await using var pipe = await ClientPipe.OpenAsync(8_910_000_001, 8_910_100_001, 0, "");
            var codec = new GameCodec();
            for (int i = 0; i < 20; i++)
            {
                await CharacterCreationHandler.SuggestNameAsync(pipe.Session);
                var frame = codec.Decode(await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer));
                Assert.Null(frame.Uid);
                Assert.Matches(NamingRule, Assert.IsType<CharacterNameSuggestionEvent>(frame.Content).Suggestion);
            }
        }
    }
}
