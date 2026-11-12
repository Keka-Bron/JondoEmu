using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol;

/// <summary>The remaining raw settings payload, captured from the actual welcome sender.</summary>
public sealed class SubscriptionTierTests
{
    private static async Task<byte[]> Settings(string language = "es")
    {
        var welcome = await WelcomeCapture.SendAsync([], withList: false, language: language);
        var packet = Assert.Single(welcome.Deliveries.OfType<WelcomeCapture.RawPacket>());
        // Settings still retain an unmapped extra. Only this boundary check inspects their bytes.
        var payload = ConnectionProtocol.ReadPayload(packet.Bytes, "hoy");
        Assert.NotNull(payload);
        return payload;
    }

    [Theory]
    [InlineData("es", "6573")]
    [InlineData("fr", "6672")]
    public async Task Settings_keep_the_measured_values_and_the_owning_sessions_language(string language, string languageHex)
    {
        Assert.Equal("081E100118013202" + languageHex.ToUpperInvariant() + "38C801",
            Convert.ToHexString(await Settings(language)));
    }

    [Fact]
    public async Task The_unsupported_subscription_marker_is_not_reintroduced()
    {
        Assert.DoesNotContain("2802", Convert.ToHexString(await Settings()));
    }

    [Fact]
    public async Task The_retained_settings_fields_include_the_unmapped_extra()
    {
        string hex = Convert.ToHexString(await Settings());
        Assert.StartsWith("081E", hex);
        Assert.Contains("1001", hex);
        Assert.Contains("1801", hex);
        Assert.Contains("32026573", hex);
        Assert.EndsWith("38C801", hex);
    }

    [Fact]
    public async Task The_settings_payload_remains_thirteen_bytes()
    {
        Assert.Equal(13, (await Settings()).Length);
    }
}
