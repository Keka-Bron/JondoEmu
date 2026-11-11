using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class EntryContinuationProtocolTests
{
    private static readonly GameCodec Codec = new();

    [Theory]
    [InlineData("1")]
    [InlineData("9000000001")]
    [InlineData("9223372036854775807")]
    public void Haapi_identity_is_a_string_event_with_no_request_uid(string id)
    {
        var payload = Text(1, id);
        var frame = Codec.Decode(CodecTests.Frame(1, "jby", payload));
        Assert.Equal(new HaapiSessionEvent(id), frame.Content);
        Assert.Null(frame.Uid);
        AssertPayload("jby", payload, new HaapiSessionEvent(id));
    }

    [Fact]
    public void Empty_gift_list_is_an_empty_event()
    {
        Assert.Empty(Assert.IsType<GameActionItemListEvent>(Codec.Decode(CodecTests.Frame(1, "jtg", [])).Content).Actions);
        AssertPayload("jtg", [], new GameActionItemListEvent([]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gift_groups_keep_titles_previews_effects_and_int64_redemption_ids(bool packed)
    {
        long[] ids = [9_000_000_001, long.MaxValue, 9_000_000_001];
        byte[] Ids(bool pack) => pack ? Msg(2, Write(writer => { foreach (long id in ids) writer.WriteInt64(id); }))
            : ids.SelectMany(id => Var(2, id)).ToArray();
        byte[] Preview(int quantity, int gid) => [.. Var(2, quantity), .. Var(3, gid)];
        byte[] Payload(bool pack) => Msg(3, [.. Msg(1, [.. Text(2, "Cadeau ☀"),
            .. Msg(3, Preview(3, 81_001)), .. Msg(3, [.. Preview(1, 81_002), .. Msg(4, Var(11, 91_001))])]), .. Ids(pack)]);
        var frame = Codec.Decode(CodecTests.Frame(1, "jtg", Payload(packed)));
        var action = Assert.Single(Assert.IsType<GameActionItemListEvent>(frame.Content).Actions);
        Assert.Equal(ids, action.ItemUids);
        var content = Assert.IsType<GameActionItemContent>(action.Content);
        Assert.Equal("Cadeau ☀", content.Title);
        Assert.Equal(new[] { (3, 81_001), (1, 81_002) }, content.Items.Select(item => (item.Quantity, item.ObjectGid)));
        Assert.Empty(content.Items[0].Effects);
        Assert.Equal(new ObjectEffect(91_001, new ObjectEffectCaseNone()), Assert.Single(content.Items[1].Effects));
        AssertPayload("jtg", Payload(true), new GameActionItemListEvent([new(new("Cadeau ☀",
            [new(3, 81_001, []), new(1, 81_002, [new(91_001, new ObjectEffectCaseNone())])]), ids)]));
    }

    [Fact]
    public void Absent_content_and_present_empty_content_remain_distinct()
    {
        var absent = Msg(3, []);
        var present = Msg(3, Msg(1, []));
        Assert.Null(Assert.Single(Assert.IsType<GameActionItemListEvent>(Codec.Decode(CodecTests.Frame(1, "jtg", absent)).Content).Actions).Content);
        var content = Assert.Single(Assert.IsType<GameActionItemListEvent>(Codec.Decode(CodecTests.Frame(1, "jtg", present)).Content).Actions).Content;
        Assert.Equal("", Assert.IsType<GameActionItemContent>(content).Title);
        Assert.Empty(content!.Items);
        AssertPayload("jtg", absent, new GameActionItemListEvent([new(null, [])]));
        AssertPayload("jtg", present, new GameActionItemListEvent([new(new("", []), [])]));
    }

    [Fact]
    public void Raw_description_is_retained_by_raw_decoding_but_omitted_by_semantic_construction()
    {
        var payload = Msg(3, Msg(1, [.. Text(2, "Gift"), .. Text(6, "Stored description")]));
        Assert.Equal(payload, Codec.DecodeRawPayload("jtg", payload).ToByteArray());
        var projected = Codec.Decode(CodecTests.Frame(1, "jtg", payload));
        Assert.Equal(Msg(3, Msg(1, Text(2, "Gift"))), Codec.ReadEnvelope(Codec.Encode(projected)).Content.Value.ToByteArray());
    }

    private static void AssertPayload(string alias, byte[] expected, GameEvent message)
    {
        var envelope = Codec.ReadEnvelope(Codec.Encode(new(message)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(expected, envelope.Content.Value.ToByteArray());
    }

    private static byte[] Var(int field, long value) => Write(writer => { writer.WriteTag(field, WireFormat.WireType.Varint); writer.WriteInt64(value); });
    private static byte[] Text(int field, string value) => Msg(field, System.Text.Encoding.UTF8.GetBytes(value));
    private static byte[] Msg(int field, byte[] value) => Write(writer => { writer.WriteTag(field, WireFormat.WireType.LengthDelimited); writer.WriteBytes(ByteString.CopyFrom(value)); });
    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }
}
