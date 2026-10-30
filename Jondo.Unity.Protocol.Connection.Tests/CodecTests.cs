using System.Text.RegularExpressions;
using Google.Protobuf;
using Jondo.Unity.Protocol.Connection.Wire;
using Jondo.Unity.Protocol.Connection.Wire.Messages;
using Xunit;

namespace Jondo.Unity.Protocol.Connection.Tests;

public sealed class CodecTests
{
    private readonly ConnectionCodec _codec = new();

    [Fact]
    public void Protocol_3_6_10_11_schema_matches_extracted_message_fields_and_referenced_enums()
    {
        string extracted = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/JondoConnectionSchema.proto"));
        var file = mhh.Descriptor.File;
        var messages = Regex.Matches(extracted, @"message\s+(\w+)\s*\{([^}]*)\}", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Assert.Equal(messages.Count, file.MessageTypes.Count);
        foreach (var message in file.MessageTypes)
        {
            var fields = Regex.Matches(messages[message.Name], @"(?:(repeated)\s+)?(\w+)\s+(\w+)\s*=\s*(\d+);");
            Assert.Equal(fields.Count, message.Fields.InDeclarationOrder().Count);
            foreach (Match match in fields)
            {
                var field = message.FindFieldByNumber(int.Parse(match.Groups[4].Value));
                Assert.NotNull(field);
                Assert.Equal(match.Groups[3].Value, field.Name);
                Assert.Equal(match.Groups[1].Success, field.IsRepeated);
                string type = field.FieldType switch
                {
                    Google.Protobuf.Reflection.FieldType.Message => field.MessageType.Name,
                    Google.Protobuf.Reflection.FieldType.Enum => field.EnumType.Name,
                    _ => field.FieldType.ToString().ToLowerInvariant()
                };
                Assert.Equal(match.Groups[2].Value, type);
            }
        }
        foreach (var item in file.EnumTypes)
        {
            string body = Regex.Match(extracted, @"enum\s+" + item.Name + @"\s*\{([^}]*)\}", RegexOptions.Singleline).Groups[1].Value;
            var values = Regex.Matches(body, @"(\w+)\s*=\s*(-?\d+);");
            Assert.Equal(values.Count, item.Values.Count);
            foreach (Match value in values) Assert.Equal(int.Parse(value.Groups[2].Value), item.FindValueByName(value.Groups[1].Value).Number);
        }
        Assert.Single(file.MessageTypes.Single(m => m.Name == "mia").Oneofs);
        Assert.True(mia.Descriptor.FindFieldByNumber(7).HasPresence);
    }

    [Fact]
    public void Sanitized_identification_and_selection_requests_use_the_existing_field_paths()
    {
        // Same shape as Jondo's former GameProtocol.proto, with invented identifiers.
        byte[] identification = new byte[] { 0x0a, 0x1e, 0x0a, 0x01, (byte)'x', 0x1a, 0x19, 0x0a, 0x03, (byte)'d', (byte)'e', (byte)'v',
            0x1a, 0x07, 0x0a, 0x05, (byte)'t', (byte)'o', (byte)'k', (byte)'e', (byte)'n', 0x2a, 0x09,
            (byte)'3', (byte)'.', (byte)'6', (byte)'.', (byte)'1', (byte)'0', (byte)'.', (byte)'1', (byte)'1' };
        var request = Assert.IsType<ConnectionRequest>(_codec.Decode(identification));
        Assert.Equal("x", request.CorrelationId);
        var content = Assert.IsType<IdentificationRequest>(request.Content);
        Assert.Equal("dev", content.DeviceIdentifier);
        Assert.Equal("token", Assert.IsType<TokenIdentification>(content.Method).Token);
        var selection = Assert.IsType<ConnectionRequest>(_codec.Decode(Convert.FromHexString("0a080a0131220308a202")));
        Assert.Equal("1", selection.CorrelationId);
        Assert.Equal(290, Assert.IsType<SelectServerRequest>(selection.Content).ServerId);
    }

    [Fact]
    public void Redirect_matches_the_former_builder_byte_for_byte()
    {
        byte[] bytes = _codec.Encode(new ConnectionResponse("0", new SelectServerResponse(
            new SelectServerSuccess("abc123", "127.0.0.1", new[] { 5555, 5555 }))));
        Assert.Equal(Convert.FromHexString("12200a0130221b0a190a0661626331323312093132372e302e302e311a04b32bb32b"), bytes);
        Assert.Equal(new[] { 5555, 5555 }, mhh.Parser.ParseFrom(bytes).Gfce.Gfcz.Gfgk.Gfgg);
    }

    [Fact]
    public void Nested_authentication_presence_and_opaque_correlation_ids_are_preserved()
    {
        var request = new ConnectionRequest("opaque-not-a-locale", new IdentificationRequest("device",
            new TokenIdentification("dummy", new ShieldCertificate(7, "hash"))));
        Assert.Equal(request, _codec.Decode(_codec.Encode(request)));
        var login = new ConnectionRequest("", new IdentificationRequest("device", new LoginIdentification("login")));
        Assert.Equal(login, _codec.Decode(_codec.Encode(login)));
        Assert.Throws<ConnectionConversionException>(() => _codec.Decode(new mhh { Gfcd = new mhj { Gfcm = new mhs() } }.ToByteArray()));
    }

    [Fact]
    public void Optional_zero_and_empty_rights_remain_distinct_from_absence()
    {
        var success = new IdentificationSuccess(1, "dummy", "0001", null, "2027-01-01T00:00:00+00:00",
            new AccountRights(false, false, false, false), 0, null, null);
        var bytes = _codec.Encode(new ConnectionResponse("uuid", new IdentificationResponse(success)));
        var wire = mhh.Parser.ParseFrom(bytes).Gfce.Gfcy.Gffu;
        Assert.True(wire.HasGfff);
        Assert.NotNull(wire.Gffe);
        Assert.Null(wire.Gffc);
        var decoded = Assert.IsType<IdentificationSuccess>(Assert.IsType<IdentificationResponse>(Assert.IsType<ConnectionResponse>(_codec.Decode(bytes)).Content).Result);
        Assert.Equal(success, decoded);
    }

    [Fact]
    public void Enum_mapping_is_explicit_and_zero_valued_error_cases_keep_their_oneof_presence()
    {
        Assert.NotEqual(0, (int)ConnectionServerStatus.Online);
        var value = new ConnectionEvent(new UpdateServerEvent(new ServerInformation(
            new Server(290, ConnectionServerStatus.Online, ServerCategory.Classic), ServerAccessibility.Accessible, [])));
        var wire = mhh.Parser.ParseFrom(_codec.Encode(value));
        Assert.Equal(0, (int)wire.Gfcf.Gfdh.Gfds.Gfhc.Gfhj);
        var error = new ConnectionResponse("id", new SelectServerResponse(new SelectServerError(SelectServerErrorReason.Refused)));
        Assert.Equal(error, _codec.Decode(_codec.Encode(error)));
        Assert.Equal(mim.GfgnOneofCase.Gfgl, mhh.Parser.ParseFrom(_codec.Encode(error)).Gfce.Gfcz.GfgnCase);
    }

    [Theory]
    [InlineData(ServerCategory.MonoAccount, "1a080a060a040a020801")]
    [InlineData(ServerCategory.Classic, "1a0a0a080a060a0408011801")]
    [InlineData(ServerCategory.PioneerMonoAccount, "1a0a0a080a060a0408011802")]
    [InlineData(ServerCategory.PioneerClassic, "1a0a0a080a060a0408011803")]
    [InlineData(ServerCategory.Tournament, "1a0a0a080a060a0408011804")]
    [InlineData(ServerCategory.Epic, "1a0a0a080a060a0408011805")]
    [InlineData(ServerCategory.Temporis, "1a0a0a080a060a0408011806")]
    public void Server_categories_match_independent_wire_fixtures_in_both_directions(ServerCategory category, string hex)
    {
        // Event{UpdateServerEvent{ServerInformation{Server{id=1, type=N}}}}.
        // Category zero is omitted by proto3, but still means MonoAccount.
        byte[] expected = Convert.FromHexString(hex);
        var value = new ConnectionEvent(new UpdateServerEvent(new ServerInformation(
            new Server(1, ConnectionServerStatus.Online, category), ServerAccessibility.Accessible, [])));
        Assert.Equal(expected, _codec.Encode(value));
        var decoded = Assert.IsType<UpdateServerEvent>(Assert.IsType<ConnectionEvent>(_codec.Decode(expected)).Content);
        Assert.NotNull(decoded.ServerInformation?.Server);
        Assert.Equal(category, decoded.ServerInformation.Server.Category);
        Assert.Equal(1, decoded.ServerInformation.Server.Id);
    }

    [Fact]
    public void Unsupported_server_categories_are_rejected()
    {
        Assert.Throws<ConnectionConversionException>(() => _codec.Decode(Convert.FromHexString("1a0a0a080a060a0408011807")));
        var value = new ConnectionEvent(new UpdateServerEvent(new ServerInformation(
            new Server(1, ConnectionServerStatus.Online, (ServerCategory)99), ServerAccessibility.Accessible, [])));
        Assert.Throws<ConnectionConversionException>(() => _codec.Encode(value));
    }

    [Fact]
    public void Unknown_wire_fields_survive_raw_decoding_but_not_stable_projection()
    {
        byte[] bytes = Convert.FromHexString("0a0412005001"); // Request{Ping}, plus undeclared request field 10.
        var raw = ConnectionBindings.DecodeWire(bytes);
        Assert.Equal(bytes, raw.ToByteArray());
        Assert.Equal(Convert.FromHexString("0a021200"), _codec.Encode(_codec.Decode(bytes)));
        Assert.Throws<ConnectionConversionException>(() => _codec.Decode([]));
        Assert.Throws<InvalidProtocolBufferException>(() => _codec.Decode(Convert.FromHexString("0a05ff")));
    }
}
