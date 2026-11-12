using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.Protocol;

/// <summary>
/// The welcome once advertised a character limit on every account, disabling creation.
/// Verify the live semantic policy rather than a second byte-built welcome sequence.
/// </summary>
public sealed class CharacterLimitFrameTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Creation_is_allowed_without_claiming_the_account_has_reached_its_limit(int count)
    {
        var characters = Enumerable.Range(0, count).Select(index => new DatabaseManager.DbCharacter
        { Id = 2000 + index, Name = "Capability" + index, Level = 1, Breed = 1 }).ToArray();
        var welcome = await WelcomeCapture.SendAsync(characters);
        Assert.Equal(new AccountCapabilitiesEvent(CharacterCreationRights.Allowed, true, Hierarchy.RolePlayer),
            Assert.Single(welcome.Messages.OfType<AccountCapabilitiesEvent>()));
    }

    [Fact]
    public async Task Rights_trust_and_session_readiness_follow_capabilities_in_order()
    {
        var welcome = await WelcomeCapture.SendAsync([], withList: false);
        int capabilities = Array.IndexOf(welcome.DeliveryTypes, typeof(AccountCapabilitiesEvent));
        Assert.True(capabilities >= 0);
        Assert.Equal(new[] { typeof(AccountRightsEvent), typeof(TrustStatusEvent), typeof(ServerSessionReadyEvent) },
            welcome.DeliveryTypes.Skip(capabilities + 1).Take(3));
        Assert.Equal(new AccountRightsEvent(new(false, false, false, false)),
            Assert.Single(welcome.Messages.OfType<AccountRightsEvent>()));
        Assert.Equal(new TrustStatusEvent(true), Assert.Single(welcome.Messages.OfType<TrustStatusEvent>()));
        Assert.Equal(new AccountSubscriptionElapsedDurationUpdateEvent(0),
            Assert.Single(welcome.Messages.OfType<AccountSubscriptionElapsedDurationUpdateEvent>()));
    }
}
