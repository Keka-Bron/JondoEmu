using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class QuestStateContractTests
{
    private readonly GameCodec _codec = new();

    [Theory]
    [InlineData("idr")]
    [InlineData("iel")]
    public void Journal_and_followed_entries_use_the_captured_objective_order_and_pending_flag(string alias)
    {
        // Quest 1629, step 2249: 9655 done, 9656 still pending.
        byte[] body = Convert.FromHexString("0a14120f0a0310b74b0a0510b84b200110c91118dd0c");
        var frame = _codec.Decode(CodecTests.Frame(1, alias, body));
        var entries = alias == "idr" ? Assert.IsType<QuestsEvent>(frame.Content).ActiveQuests : Assert.IsType<QuestsFollowedEvent>(frame.Content).Quests;
        var entry = Assert.Single(entries);
        Assert.Equal(1629, entry.QuestId);
        Assert.Equal(2249, entry.Details!.StepId);
        Assert.Equal(new[] { 9655, 9656 }, entry.Details.Objectives.Select(objective => objective.ObjectiveId));
        Assert.Equal(new[] { false, true }, entry.Details.Objectives.Select(objective => objective.ObjectivePending));
        Assert.All(entry.Details.Objectives, objective => { Assert.Null(objective.Completion); Assert.Empty(objective.DialogParams); });
        GameEvent expected = alias == "idr" ? new QuestsEvent([entry], [], []) : new QuestsFollowedEvent([entry]);
        AssertBody(alias, body, expected);
    }

    [Fact]
    public void First_captured_journal_entry_retains_its_quest_step_and_pending_objective()
    {
        byte[] body = Convert.FromHexString("0a0f120a0a05109f45200110ec1018800c");
        var expected = new QuestsEvent([new(new([new([], 8863, null, true)], 2156), 1536)], [], []);
        var entry = Assert.Single(Assert.IsType<QuestsEvent>(_codec.Decode(CodecTests.Frame(1, "idr", body)).Content).ActiveQuests);
        Assert.Equal(1536, entry.QuestId);
        Assert.Equal(2156, entry.Details!.StepId);
        Assert.Equal(8863, Assert.Single(entry.Details.Objectives).ObjectiveId);
        AssertBody("idr", body, expected);
    }

    [Fact]
    public void Finished_ids_are_retained_while_the_unmapped_historical_constant_is_deliberately_omitted()
    {
        byte[] historical = Convert.FromHexString("1a0508011080101a0508011081101a0508011082102200");
        // Raw parsing retains the unmapped nested constants, but protobuf itself
        // normalizes away the explicit empty packed list at the end.
        Assert.Equal(historical[..^2], _codec.DecodeRawPayload("idr", historical).ToByteArray());
        var journal = Assert.IsType<QuestsEvent>(_codec.Decode(CodecTests.Frame(1, "idr", historical)).Content);
        Assert.Equal(new[] { 2048, 2049, 2050 }, journal.FinishedQuests.Select(quest => quest.QuestId));
        Assert.Empty(journal.ActiveQuests);
        Assert.Empty(journal.ReinitializedDoneQuestsId);
        AssertBody("idr", Convert.FromHexString("1a031080101a031081101a03108210"),
            new QuestsEvent([], [new(2048), new(2049), new(2050)], []));
    }

    [Theory]
    [InlineData("idr")]
    [InlineData("iel")]
    [InlineData("iom")]
    public void Empty_replacements_have_an_event_envelope_even_with_no_payload_fields(string alias)
    {
        GameEvent message = alias switch
        {
            "idr" => new QuestsEvent([], [], []),
            "iel" => new QuestsFollowedEvent([]),
            _ => new NpcsMapQuestStatusUpdateEvent([]),
        };
        AssertBody(alias, [], message);
        var frame = _codec.Decode(CodecTests.Frame(1, alias, []));
        Assert.Equal(message.GetType(), frame.Content.GetType());
        Assert.Null(frame.Uid);
        // The former writer's explicit empty packed list has the same semantic value.
        if (alias == "idr") Assert.Empty(Assert.IsType<QuestsEvent>(_codec.Decode(CodecTests.Frame(1, alias, [0x22, 0x00])).Content).ReinitializedDoneQuestsId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Absent_and_present_empty_active_details_are_distinct(bool present)
    {
        byte[] body = present ? [0x0a, 0x02, 0x12, 0x00] : [0x0a, 0x00];
        var journal = Assert.IsType<QuestsEvent>(_codec.Decode(CodecTests.Frame(1, "idr", body)).Content);
        var entry = Assert.Single(journal.ActiveQuests);
        Assert.Equal(present, entry.Details != null);
        AssertBody("idr", body, new QuestsEvent([new(present ? new([], 0) : null, 0)], [], []));
    }

    [Theory]
    [InlineData("0a16120f1202fb1220e0e3feffffffffffff01188294d068", 219417090L, 2427, 0)]
    [InlineData("0a16120f1a02c61320e0e3feffffffffffff011881b09073", 241440769L, 0, 2502)]
    public void Captured_mark_payloads_bind_contextual_npc_ids_and_both_quest_lists(string hex, long mapId, int offer, int waiting)
    {
        byte[] body = Convert.FromHexString(hex);
        var marks = Assert.IsType<NpcsMapQuestStatusUpdateEvent>(_codec.Decode(CodecTests.Frame(1, "iom", body)).Content);
        var map = Assert.Single(marks.MapInformation);
        Assert.Equal(mapId, map.MapId);
        var npc = Assert.Single(map.NpcsWithQuests);
        Assert.Equal(-20_000, npc.NpcId);
        Assert.Equal(offer == 0 ? [] : new[] { offer }, npc.QuestsToStart);
        Assert.Equal(waiting == 0 ? [] : new[] { waiting }, npc.QuestsToValidate);
        AssertBody("iom", body, new NpcsMapQuestStatusUpdateEvent([new([new(offer == 0 ? [] : [offer], waiting == 0 ? [] : [waiting], -20_000)], mapId)]));
    }

    [Fact]
    public void Multiple_mark_entries_keep_both_packed_list_orders_and_full_numeric_widths()
    {
        byte[] body = Msg(1, [.. Msg(2, [.. Packed(2, [2511, 26]), .. Packed(3, [2502, 1629]), .. Var(4, int.MinValue)]),
            .. Msg(2, [.. Packed(3, [73]), .. Var(4, int.MaxValue)]), .. Var(3, long.MaxValue)]);
        var marks = Assert.IsType<NpcsMapQuestStatusUpdateEvent>(_codec.Decode(CodecTests.Frame(1, "iom", body)).Content);
        var map = Assert.Single(marks.MapInformation);
        Assert.Equal(long.MaxValue, map.MapId);
        Assert.Equal(new[] { int.MinValue, int.MaxValue }, map.NpcsWithQuests.Select(npc => npc.NpcId));
        Assert.Equal(new[] { 2511, 26 }, map.NpcsWithQuests[0].QuestsToStart);
        Assert.Equal(new[] { 2502, 1629 }, map.NpcsWithQuests[0].QuestsToValidate);
        AssertBody("iom", body, new NpcsMapQuestStatusUpdateEvent([new([new([2511, 26], [2502, 1629], int.MinValue), new([], [73], int.MaxValue)], long.MaxValue)]));
    }

    private void AssertBody(string alias, byte[] body, GameEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Equal(alias, envelope.Alias);
        Assert.Null(envelope.Uid);
        Assert.Equal(body, envelope.Content.Value.ToByteArray());
    }

    private static byte[] Var(int tag, long value) => Write(output => { output.WriteTag(tag, WireFormat.WireType.Varint); output.WriteInt64(value); });
    private static byte[] Msg(int tag, byte[] body) => Write(output => { output.WriteTag(tag, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(body)); });
    private static byte[] Packed(int tag, int[] values) => Msg(tag, Write(output => { foreach (int value in values) output.WriteInt32(value); }));
    private static byte[] Write(Action<CodedOutputStream> action)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true)) action(output);
        return stream.ToArray();
    }
}
