using Jondo.Unity.Server.Network;
using Thrift;
using Thrift.Protocol;
using Thrift.Transport;
using Thrift.Transport.Client;
using Xunit;
using Zaap;

namespace Jondo.Unity.Server.Tests;

public sealed class NativeZaapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_availability_is_a_present_boolean_even_when_false(bool available)
    {
        using var bytes = new MemoryStream();
        using var transport = new TStreamTransport(bytes, bytes, new TConfiguration());
        var protocol = new TBinaryProtocol(transport);
        var result = new ZaapService.InternalStructs.updater_isUpdateAvailable_result { Success = available };
        await result.WriteAsync(protocol, CancellationToken.None);
        // Independent Thrift binary encoding: BOOL, field 0, value, STOP.
        Assert.Equal(new byte[] { 2, 0, 0, available ? (byte)1 : (byte)0, 0 }, bytes.ToArray());
        bytes.Position = 0;
        var decoded = new ZaapService.InternalStructs.updater_isUpdateAvailable_result();
        await decoded.ReadAsync(protocol, CancellationToken.None);
        Assert.True(decoded.__isset.success);
        Assert.Equal(available, decoded.Success);
        Assert.Equal(available, decoded.DeepCopy().Success);
    }

    [Fact]
    public async Task Local_emulator_never_requires_an_official_client_update()
        => Assert.False(await new ZaapHandler().updater_isUpdateAvailable("local-session"));
}
