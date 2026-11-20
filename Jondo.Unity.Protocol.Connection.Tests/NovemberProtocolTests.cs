#if JONDO_PROTOCOL_3_8_1_2
using Jondo.Unity.Protocol.Connection.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Connection.Tests;

public sealed class NovemberProtocolTests
{
    private readonly ConnectionCodec _codec = new();

    [Fact]
    public void Token_identification_uses_the_November_method_tag_and_preserves_correlation()
    {
        // Message.request(1), Request.identification(3), IdentificationRequest.token_request(3).
        byte[] wire = Convert.FromHexString("0A0F0A01611A0A0A01641A050A03746F6B");
        var expected = new ConnectionRequest("a", new IdentificationRequest("d", new TokenIdentification("tok", null)));
        Assert.Equal(expected, _codec.Decode(wire));
        Assert.Equal(wire, _codec.Encode(expected));

        // August used tag 2 for the token branch. It must not be interpreted as a November login.
        wire[10] = 0x12;
        Assert.Throws<ConnectionConversionException>(() => _codec.Decode(wire));
    }

    [Fact]
    public void Server_selection_and_redirect_keep_their_correlation_and_fields()
    {
        byte[] request = Convert.FromHexString("0A080A0173220308A202");
        Assert.Equal(new ConnectionRequest("s", new SelectServerRequest(290)), _codec.Decode(request));

        var reply = new ConnectionResponse("s", new SelectServerResponse(new SelectServerSuccess("t", "h", [5555])));
        byte[] expected = Convert.FromHexString("12110A0173220C0A0A0A01741201681A02B32B");
        Assert.Equal(expected, _codec.Encode(reply));
        var response = Assert.IsType<ConnectionResponse>(_codec.Decode(expected));
        Assert.Equal("s", response.CorrelationId);
        var redirect = Assert.IsType<SelectServerSuccess>(Assert.IsType<SelectServerResponse>(response.Content).Result);
        Assert.Equal("t", redirect.Ticket);
        Assert.Equal("h", redirect.Host);
        Assert.Equal(new[] { 5555 }, redirect.Ports);
    }

    [Fact]
    public void An_unbound_November_enum_value_fails_semantic_conversion()
    {
        // SelectServerResponse.error (oneof tag 2), ServerNotAllowed (wire value 4).
        Assert.Throws<ConnectionConversionException>(() => _codec.Decode(Convert.FromHexString("1207220210040A0161")));
    }
}
#endif
