using Google.Protobuf;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GameEntryTests
{
    private static readonly GameCodec Codec = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Weekly_reset_uses_the_owning_codec_and_the_decoded_request_uid(int uid)
    {
        var codec = new GameMessagingTests.CountingCodec();
        byte[]? sent = null;
        var session = new GameSession(codec, bytes => { sent = bytes; return Task.CompletedTask; });
        // Independent request envelope, rather than asking the codec to produce its own input.
        byte[] request = Pb.New().Msg(2, Pb.New()
            .Msg(1, Pb.New().Str(1, ConnectionProtocol.UriPrefix + "jew").Bytes(2, []))
            .Var(2, uid)).Build();
        var decoded = GameEntryDispatch.Decode(session, request, ConnectionProtocol.UriPrefix + "jew");
        Assert.IsType<GuildWeeklyResetDateRequest>(decoded!.Content);
        var before = GuildProtocol.BuildWeeklyReset(DateTime.UtcNow);
        await Jondo.Unity.Server.Handlers.GuildHandler.WeeklyResetAsync(session, decoded.Uid!.Value);
        var after = GuildProtocol.BuildWeeklyReset(DateTime.UtcNow);
        Assert.Equal(1, codec.Decodes);
        Assert.Equal(1, codec.Encodes);
        var envelope = Codec.ReadEnvelope(sent!);
        Assert.Equal(GameMessageKind.Response, envelope.Kind);
        Assert.Equal("jez", envelope.Alias);
        Assert.Equal(uid, envelope.Uid);
        var response = Assert.IsType<GuildWeeklyResetDateResponse>(Codec.Decode(sent!).Content);
        Assert.Contains(response, new[] { before, after });
        Assert.Equal(Pb.New().Str(1, response.ResetDate).Build(), envelope.Content.Value.ToByteArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Entry_requests_use_session_codec_and_preserve_first_selection_semantics(int uid)
    {
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, _ => Task.CompletedTask);
        GameRequest[] requests = [new IdentificationRequest("ticket", "fr"), new CharacterListRequest(),
            new CharacterSelectionRequest(987654321), new CharacterFirstSelectionRequest(true, 987654321),
            new SequenceNumberRequest(12), new ContextCreationRequest(), new ContextReadyRequest(42),
            new MapInformationRequest(42), new PingRequest(false), new FightMapRequest(),
            new CharacterForceSelectionReadyRequest(), new GuildWeeklyResetDateRequest()];
        foreach (var request in requests)
        {
            byte[] bytes = Codec.Encode(new(request, uid));
            var decoded = GameEntryDispatch.Decode(session, bytes, Codec.ReadEnvelope(bytes).Content.TypeUrl);
            Assert.NotNull(decoded);
            Assert.Equal(request, decoded.Content);
            Assert.Equal(uid, decoded.Uid);
        }
        Assert.Equal(requests.Length, codec.Decodes);
        Assert.Null(GameEntryDispatch.Decode(session, Codec.Encode(new(new MapMovementConfirmRequest(), uid)),
            ConnectionProtocol.UriPrefix + "jqi"));
    }

    [Fact]
    public void Wrong_envelopes_and_malformed_owned_requests_do_not_fall_back_to_legacy_parsing()
    {
        var session = new GameSession(Codec, _ => Task.CompletedTask);
        // Old selection tests used event envelopes for requests; the typed boundary rejects them.
        byte[] eventEnvelope = ConnectionProtocol.Push("kvw", Pb.New().Var(1, 42).Build());
        Assert.Throws<GameConversionException>(() => GameEntryDispatch.Decode(session, eventEnvelope,
            ConnectionProtocol.UriPrefix + "kvw"));
        Assert.Throws<InvalidProtocolBufferException>(() => GameEntryDispatch.Decode(session, [0xff],
            ConnectionProtocol.UriPrefix + "kqz"));
    }

    [Fact]
    public void Ticket_binding_is_single_use_keeps_launch_language_and_entry_requires_a_character()
    {
        var first = new GameSession(Codec, _ => Task.CompletedTask);
        var second = new GameSession(Codec, _ => Task.CompletedTask);
        var ticket = SessionRegistry.Issue(90_000_001, 290, "fr");
        var identify = new IdentificationRequest(ticket.Value, "en");
        Assert.False(GameEntryDispatch.IsAllowed(first, new CharacterSelectionRequest(42)));
        Assert.False(GameEntryDispatch.IsAllowed(first, new CharacterForceSelectionReadyRequest()));
        Assert.False(GameEntryDispatch.IsAllowed(first, new FightMapRequest()));
        Assert.True(GameEntryDispatch.BindTicket(first, identify));
        Assert.False(GameEntryDispatch.BindTicket(second, identify));
        Assert.Equal(90_000_001, first.AccountId);
        Assert.Equal(290, first.ServerId);
        Assert.Equal("fr", first.State.Language);
        Assert.False(GameEntryDispatch.IsAllowed(first, identify));
        Assert.True(GameEntryDispatch.IsAllowed(first, new CharacterFirstSelectionRequest(false, 42)));
        Assert.True(GameEntryDispatch.IsAllowed(first, new CharacterForceSelectionReadyRequest()));
        Assert.False(GameEntryDispatch.IsAllowed(first, new FightMapRequest()));
        Assert.False(GameEntryDispatch.IsAllowed(first, new GuildWeeklyResetDateRequest()));
        Assert.False(GameEntryDispatch.IsAllowed(first, new SequenceNumberRequest(1)));
        first.State.CharacterId = 42;
        first.EnterWorld();
        Assert.True(GameEntryDispatch.IsAllowed(first, new SequenceNumberRequest(1)));
        Assert.True(GameEntryDispatch.IsAllowed(first, new MapInformationRequest(42)));
        Assert.True(GameEntryDispatch.IsAllowed(first, new FightMapRequest()));
        Assert.True(GameEntryDispatch.IsAllowed(first, new GuildWeeklyResetDateRequest()));
        Assert.False(GameEntryDispatch.IsAllowed(first, new CharacterForceSelectionReadyRequest()));
        Assert.False(GameEntryDispatch.IsAllowed(first, new CharacterSelectionRequest(43)));
        Assert.False(second.IsAuthenticated);
    }

    [Fact]
    public async Task Welcome_uses_typed_sends_in_existing_order_and_preserves_partial_packets()
    {
        var packets = new List<byte[]>();
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, bytes => { packets.Add(bytes); return Task.CompletedTask; });
        session.BindAccount(90_000_002, 290, "fr");
        await ConnectionProtocol.SendWelcomeAsync(session, [], withList: false);
        Assert.Equal(new[] { "kra", "lqu", "hoy", "kqu", "mgq", "mgt", "hpd", "krs", "mgz" },
            packets.Select(p => Codec.ReadEnvelope(p).Alias));
        Assert.Equal(8, codec.Encodes);
        Assert.All(packets, p => Assert.Equal(GameMessageKind.Event, Codec.ReadEnvelope(p).Kind));
        Assert.Equal("fr", Assert.IsType<ServerSettingsEvent>(Codec.Decode(packets[2]).Content).Language);
        Assert.Equal(new AccountCapabilitiesEvent(CharacterCreationRights.Allowed, true, Hierarchy.RolePlayer),
            Codec.Decode(packets[4]).Content);
        Assert.Equal(new AccountRightsEvent(new(false, false, false, false)), Codec.Decode(packets[5]).Content);
        Assert.Equal(new TrustStatusEvent(true), Codec.Decode(packets[6]).Content);
        Assert.Equal(new ServerSessionReadyEvent(), Codec.Decode(packets[7]).Content);
        Assert.Equal(new AccountSubscriptionElapsedDurationUpdateEvent(0), Codec.Decode(packets[8]).Content);
        Assert.Empty(Codec.ReadEnvelope(packets[8]).Content.Value);
        Assert.Contains(ProtoMessage.Parse(Codec.ReadEnvelope(packets[2]).Content.Value.ToByteArray()).Fields,
            f => f.FieldNumber == 7 && f.VarIntValue == 200);
    }

    [Theory]
    [InlineData("kra")]
    [InlineData("kml")]
    [InlineData("krs")]
    [InlineData("lqg")]
    [InlineData("lva")]
    public async Task Empty_mapped_entry_events_use_the_owning_codec(string alias)
    {
        var codec = new GameMessagingTests.CountingCodec();
        byte[]? sent = null;
        var session = new GameSession(codec, bytes => { sent = bytes; return Task.CompletedTask; });
        byte[] original = ConnectionProtocol.Push(alias);
        await GameEntryMessages.SendAsync(session, original);
        Assert.Equal(1, codec.Encodes);
        Assert.Equal(alias, Codec.ReadEnvelope(sent!).Alias);
        Assert.Equal(GameMessageKind.Event, Codec.ReadEnvelope(sent!).Kind);
    }

    [Fact]
    public async Task Semantic_projection_handles_field_order_and_packed_repeated_values()
    {
        // Independently authored field order and unpacked list; byte equality would reject both.
        byte[][] packets = [ConnectionProtocol.Push("lqu", Pb.New().Var(2, 123456789).Var(1, 120).Build()),
            ConnectionProtocol.Push("kqu", Pb.New().Var(1, 3).Var(1, 7).Var(1, 13).Build()),
            ConnectionProtocol.Push("jru", Pb.New().Var(2, 154010371).Build())];
        var codec = new GameMessagingTests.CountingCodec();
        var sent = new List<byte[]>();
        var session = new GameSession(codec, bytes => { sent.Add(bytes); return Task.CompletedTask; });
        foreach (var packet in packets) await GameEntryMessages.SendAsync(session, packet);
        Assert.Equal(3, codec.Encodes);
        Assert.Equal(new TimeEvent(120, 123456789), Codec.Decode(sent[0]).Content);
        Assert.Equal(new[] { 3, 7, 13 }, Assert.IsType<OptionalFeaturesEvent>(Codec.Decode(sent[1]).Content).Features);
        Assert.Equal(new MapCurrentEvent(154010371), Codec.Decode(sent[2]).Content);
    }

    [Fact]
    public async Task Partial_unknown_and_malformed_packets_retain_exact_original_bytes()
    {
        var codec = new GameMessagingTests.CountingCodec();
        var sent = new List<byte[]>();
        var session = new GameSession(codec, bytes => { sent.Add(bytes); return Task.CompletedTask; });
        byte[][] originals = [
            ConnectionProtocol.Push("hpd", Pb.New().Var(1, 1).Var(99, 7).Build()),
            ConnectionProtocol.Push("mgz", Pb.New().Var(1, 123).Var(99, 7).Build()),
            ConnectionProtocol.Push("jss", Pb.New().Var(2, 42).Var(6, 450).EmptyMsg(13)
                .Msg(11, Pb.New().Var(5, 123)).Msg(15, Pb.New().Var(3, 123))
                .Msg(9, Pb.New().Var(2, 12).Var(4, 34)
                    .Msg(7, Pb.New().Msg(2, Pb.New().Var(2, 1).Var(12, 56)))).Build()),
            ConnectionProtocol.Push("unknown-entry-event", [1, 2]),
            [0xff],
            ConnectionProtocol.Push("jru", Pb.New().Var(2, 42).Var(99, 7).Build()),
            [.. ConnectionProtocol.Push("jru", Pb.New().Var(2, 42).Build()), .. Pb.New().Var(99, 7).Build()],
        ];
        foreach (var original in originals) await GameEntryMessages.SendAsync(session, original);
        Assert.Equal(0, codec.Encodes);
        for (int i = 0; i < originals.Length; i++) Assert.Same(originals[i], sent[i]);
    }

    [Fact]
    public void Character_list_and_selection_dates_project_but_unknown_nested_look_fields_are_preserved()
    {
        Pb Character(bool dates, bool extraLook) => Pb.New().Msg(1, Pb.New().Str(2, "Test").Var(3, 10)
            .Msg(4, Traits(dates, extraLook))).Var(2, 987654321);
        Pb Traits(bool dates, bool extraLook)
        {
            var look = Pb.New().Var(2, 3).Var(3, 1);
            if (extraLook) look.Var(99, 7);
            var traits = Pb.New().EmptyMsg(2).Msg(6, look).Var(7, 1);
            if (dates) traits.Str(1, "2026-08-01T12:00:00.000Z").Str(4, "2026-08-11T12:00:00.000Z");
            return traits;
        }
        byte[] list = ConnectionProtocol.Push("kvi", Pb.New().Msg(1, Character(false, false)).Build());
        Assert.True(GameEntryMessages.TryProject(list, out var projected));
        Assert.Equal(987654321, Assert.Single(Assert.IsType<CharacterListEvent>(projected).Characters).Id);
        byte[] selected = ConnectionProtocol.Push("kva", Pb.New().Msg(1, Pb.New().Msg(1, Character(true, false))).Build());
        Assert.True(GameEntryMessages.TryProject(selected, out var selection));
        var character = Assert.IsType<CharacterSelectionEventCaseSuccess>(selection).Value.Character;
        var basic = Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(character.ComplementCase).Value;
        var look = Assert.IsType<CharacterBasicInformationComplementCaseCharacterLook>(basic.ComplementCase).Value;
        Assert.Equal("2026-08-01T12:00:00.000Z", look.LastDisconnectionDate);
        Assert.Equal("2026-08-11T12:00:00.000Z", look.LastConnectionDate);
        byte[] withUnknownLook = ConnectionProtocol.Push("kvi", Pb.New().Msg(1, Character(false, true)).Build());
        Assert.False(GameEntryMessages.TryProject(withUnknownLook, out _));
    }

    [Fact]
    public async Task Plain_map_with_interactives_states_and_no_specific_case_uses_typed_send()
    {
        byte[] original = ConnectionProtocol.Push("jss", Pb.New().Var(2, 42).Var(6, 450)
            .Msg(11, Pb.New().Var(1, 1).Var(5, 123).Var(6, -1)
                .Msg(4, Pb.New().Var(1, 1001).Var(2, 114)))
            .Msg(15, Pb.New().Var(1, 1).Var(2, 200).Var(3, 123).Var(4, 2)).Build());
        var codec = new GameMessagingTests.CountingCodec();
        byte[]? sent = null;
        var session = new GameSession(codec, bytes => { sent = bytes; return Task.CompletedTask; });
        await GameEntryMessages.SendAsync(session, original);
        Assert.Equal(1, codec.Encodes);
        var map = Assert.IsType<MapComplementaryInformationEvent>(Codec.Decode(sent!).Content);
        Assert.IsType<MapSpecificComplementaryInformationCaseNone>(map.SpecificCase);
        var element = Assert.Single(map.InteractiveElements);
        Assert.True(element.OnCurrentMap);
        Assert.Equal(-1, element.ElementTypeId);
        Assert.Equal(new InteractiveElementSkill(114, 1001, null), Assert.Single(element.EnabledSkills));
        Assert.Equal(new StatedElement(123, 200, 2, true), Assert.Single(map.StatedElements));
        Assert.Equal(Codec.DecodeRawPayload("jss", Codec.ReadEnvelope(original).Content.Value.Span),
            Codec.DecodeRawPayload("jss", Codec.ReadEnvelope(sent!).Content.Value.Span));
    }

    [Fact]
    public async Task Independent_entry_fixtures_preserve_order_and_all_unprojected_bytes()
    {
        // Small independent payloads exercise legacy-builder projection without a
        // recorded account or the deleted world-entry manifest.
        (string Alias, byte[] Body)[] fixtures = [
            ("kqp", []), ("kqy", []), ("kml", []),
            ("kmp", Pb.New().Var(1, 1).Build()),
            ("jru", Pb.New().Var(2, 71_001).Build()),
            ("lqu", Pb.New().Var(2, 123).Build()),
            ("ktq", Pb.New().Var(2, 1).Var(3, 9).Build()),
            ("lru", []),
            ("hlm", [0x12, 0x04, 0x0a, 0x00, 0x12, 0x00]),
            ("iwd", Pb.New().Var(2, 51_001).Packed(3, [81_001]).Build()),
            ("iun", Pb.New().Var(1, 17).Var(3, 1200).Build()),
            ("hnv", [8, 1]), ("ivo", Pb.New().Var(1, 9_000_000_001).Build()),
            ("lal", []), ("kvx", []),
            ("khn", [0x0a, 4, 1, 97, 98, 127]), ("khi", [8, 97]),
            ("jco", [0x12, 4, 0x1a, 0, 0x22, 0]),
            ("lty", Pb.New().Str(2, "1970-01-01T00:00:00.000Z").EmptyMsg(3).Build()),
            ("kto", [0x0a, 2, 0, 9, 0x12, 1, 11]),
            ("hjk", Pb.New().Packed(1, [9_000_000_001, 71_001]).Build()),
            ("lqd", Pb.New().Var(1, 81_001).Build()),
            ("lzp", Pb.New().Msg(1, Pb.New().Var(2, 81_001).Var(4, 1)).Build()),
            ("irq", Pb.New().Msg(1, Pb.New().Var(1, 81_001).Var(2, 60).Var(3, 2).Var(4, 20).Var(5, 23)).Build()),
        ];
        byte[][] originals = [.. fixtures.Select(fixture => ConnectionProtocol.Push(fixture.Alias, fixture.Body)),
            ConnectionProtocol.Push("unknown-entry-event", [1, 2]), [0xff]];
        var codec = new GameMessagingTests.CountingCodec();
        var packets = new List<byte[]>();
        var session = new GameSession(codec, bytes => { packets.Add(bytes); return Task.CompletedTask; });
        var converted = new HashSet<string>();
        foreach (var original in originals)
        {
            int before = packets.Count;
            int encodes = codec.Encodes;
            await GameEntryMessages.SendAsync(session, original);
            Assert.Equal(before + 1, packets.Count);
            if (codec.Encodes == encodes)
                Assert.Same(original, packets[^1]);
            else
            {
                var envelope = Codec.ReadEnvelope(original);
                converted.Add(envelope.Alias);
                var sent = Codec.ReadEnvelope(packets[^1]);
                Assert.Equal(envelope.Kind, sent.Kind);
                Assert.Equal(envelope.Uid, sent.Uid);
                Assert.Equal(envelope.Content.TypeUrl, sent.Content.TypeUrl);
                Assert.Equal(Codec.DecodeRawPayload(envelope.Alias, envelope.Content.Value.Span),
                    Codec.DecodeRawPayload(sent.Alias, sent.Content.Value.Span));
            }
        }
        foreach (string alias in fixtures.Select(fixture => fixture.Alias))
            Assert.Contains(alias, converted);
    }
}
