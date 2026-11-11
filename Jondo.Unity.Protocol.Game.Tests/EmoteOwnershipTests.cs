using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class EmoteOwnershipTests
{
    private readonly GameCodec _codec = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Packed_and_unpacked_lists_preserve_order_duplicates_and_full_int32_ids(bool packed)
    {
        int[] ids = [81_003, 81_001, 81_003, int.MaxValue];
        byte[] canonical = Packed(ids);
        byte[] input = packed ? canonical : ids.SelectMany(id => Var(1, id)).ToArray();
        var decoded = _codec.Decode(CodecTests.Frame(1, "khn", input));
        Assert.Null(decoded.Uid);
        Assert.Equal(ids, Assert.IsType<EmotesEvent>(decoded.Content).EmotesId);
        AssertEncoded("khn", canonical, new EmotesEvent(ids));
    }

    [Fact]
    public void Empty_list_is_an_explicit_empty_semantic_snapshot()
    {
        Assert.Empty(Assert.IsType<EmotesEvent>(_codec.Decode(CodecTests.Frame(1, "khn", [])).Content).EmotesId);
        AssertEncoded("khn", [], new EmotesEvent([]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(81_001)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Learned_notification_binds_the_emote_id_as_int32(int id)
    {
        byte[] body = id == 0 ? [] : Var(1, id);
        Assert.Equal(new EmoteAddedEvent(id), _codec.Decode(CodecTests.Frame(1, "khi", body)).Content);
        AssertEncoded("khi", body, new EmoteAddedEvent(id));
    }

    [Fact]
    public void Historical_starting_list_and_guild_banner_fixtures_match_the_typed_payloads()
    {
        // Creation capture's four starting attitudes; guild-founding capture's learned banner.
        AssertEncoded("khn", Convert.FromHexString("0a040161627f"), new EmotesEvent([1, 97, 98, 127]));
        AssertEncoded("khi", Convert.FromHexString("0861"), new EmoteAddedEvent(97));
    }

    private void AssertEncoded(string alias, byte[] body, GameEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(body, envelope.Content.Value.ToByteArray());
    }

    private static byte[] Packed(int[] ids)
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, leaveOpen: true))
        {
            writer.WriteTag(1, WireFormat.WireType.LengthDelimited);
            writer.WriteLength(ids.Sum(CodedOutputStream.ComputeInt32Size));
            foreach (int id in ids) writer.WriteInt32(id);
        }
        return stream.ToArray();
    }

    private static byte[] Var(int field, int value)
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, leaveOpen: true))
        {
            writer.WriteTag(field, WireFormat.WireType.Varint);
            writer.WriteInt32(value);
        }
        return stream.ToArray();
    }
}
