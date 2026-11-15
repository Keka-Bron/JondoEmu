using Jondo.Unity.Protocol.Game;
using Xunit;

namespace Jondo.Unity.Tests.Protocol;

/// <summary>Semantic welcome settings, independent of subscription and character limits.</summary>
public sealed class SubscriptionTierTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("en")]
    public async Task Settings_keep_the_existing_policy_and_the_owning_sessions_language(string language)
    {
        var welcome = await WelcomeCapture.SendAsync([], withList: false, language: language);

        Assert.Equal(new ServerSettingsEvent(30, ServerType.Classical, true, false, 0, language),
            Assert.Single(welcome.Messages.OfType<ServerSettingsEvent>()));
    }
}
