using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.Protocol;

/// <summary>Live welcome ordering and the ordinary character-selection screen.</summary>
public sealed class WelcomeBurstTests
{
    private static DatabaseManager.DbCharacter[] Characters(int count)
        => Enumerable.Range(0, count).Select(index => new DatabaseManager.DbCharacter
        { Id = 1000 + index, Name = "Prueba" + index, Level = 1, Breed = 1 }).ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task The_ordinary_character_screen_does_not_force_selection(int characters)
    {
        var welcome = await WelcomeCapture.SendAsync(Characters(characters));
        Assert.DoesNotContain(welcome.Messages, message => message is CharacterForceSelectionEvent);
        Assert.Equal(characters, Assert.Single(welcome.Messages.OfType<CharacterListEvent>()).Characters.Count);
    }

    [Fact]
    public async Task Gifts_are_delivered_immediately_after_the_character_list()
    {
        var welcome = await WelcomeCapture.SendAsync(Characters(2));
        int list = Array.IndexOf(welcome.DeliveryTypes, typeof(CharacterListEvent));
        Assert.True(list >= 0, "The welcome must include the character list.");
        Assert.IsType<GameActionItemListEvent>(welcome.Deliveries[list + 1].Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_live_welcome_preserves_order_and_conditionally_includes_the_list_refresh(bool withList)
    {
        var welcome = await WelcomeCapture.SendAsync(Characters(1), withList);
        var expected = new List<Type>
        {
            typeof(AuthenticationTicketAcceptedEvent), typeof(TimeEvent), typeof(ServerSettingsEvent),
            typeof(OptionalFeaturesEvent), typeof(AccountCapabilitiesEvent), typeof(AccountRightsEvent),
            typeof(TrustStatusEvent), typeof(ServerSessionReadyEvent), typeof(AccountSubscriptionElapsedDurationUpdateEvent),
        };
        if (withList)
            expected.AddRange([typeof(QueueStatusEvent), typeof(QueueStatusEvent), typeof(QueueStatusEvent),
                typeof(CharacterListEvent), typeof(GameActionItemListEvent)]);
        Assert.Equal(expected, welcome.DeliveryTypes);
        Assert.All(welcome.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(new QueueStatusEvent[] { new(1, 1), new(1, 0), new(0, 0) }.Take(withList ? 3 : 0),
            welcome.Messages.OfType<QueueStatusEvent>());
    }

    [Fact]
    public async Task An_empty_account_receives_the_same_sequence_with_an_empty_list()
    {
        var populated = await WelcomeCapture.SendAsync(Characters(1));
        var empty = await WelcomeCapture.SendAsync([]);
        Assert.Equal(populated.DeliveryTypes, empty.DeliveryTypes);
        Assert.Empty(Assert.Single(empty.Messages.OfType<CharacterListEvent>()).Characters);
    }
}
