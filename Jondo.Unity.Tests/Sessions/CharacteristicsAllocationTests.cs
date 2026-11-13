using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions;

[Collection("game-node-listener")]
public sealed class CharacteristicsAllocationTests
{
    private static GameSession Ready(List<GameFrame> frames)
    {
        var codec = new GameCodec();
        var session = new GameSession(codec, bytes => { frames.Add(codec.Decode(bytes)); return Task.CompletedTask; });
        session.BindAccount(8_941_790_001, 1);
        session.State.CharacterId = 8_941_790_002;
        session.State.Breed = 8;
        session.State.CharacterLevel = 200;
        session.State.CharacterRemainingPoints = 995;
        session.State.ScrolledStrength = 100;
        session.EnterWorld();
        return session;
    }

    private static CharacterCharacteristicDetailed Detail(CharacterCharacteristics sheet, int id)
        => Assert.IsType<CharacterCharacteristicCaseDetailed>(sheet.Characteristics.Single(s => s.CharacteristicId == id).CharacteristicCase).Value;

    [Fact]
    public async Task Production_loop_spends_and_resets_through_typed_requests_and_persists_the_owner()
    {
        const long account = 9_100_870_002;
        long character = DatabaseManager.CreateCharacter(account, 1, "Typed-Stats", 8, 0, 1,
            [-1, -1, -1, -1, -1], 191105026, 200, 0, 0, Array.Empty<(int, int)>());
        Assert.True(character > 0);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        Task loop = Task.CompletedTask;
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var clientStream = client.GetStream();
            using var server = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var session = new GameSession(server.GetStream());
            session.BindAccount(account, 1);
            using (SessionContext.Push(session)) Assert.True(DatabaseManager.LoadCharacter(character));
            session.EnterWorld();
            var codec = new GameCodec();
            async Task<GameEvent> Next() => Assert.IsAssignableFrom<GameEvent>(codec.Decode(
                await Jondo.Protocol.NetworkMessage.ReadFrameAsync(clientStream).WaitAsync(TimeSpan.FromSeconds(10))).Content);
            GameNodeProxy.Start(0);
            byte[] first = codec.Encode(new(new CharacterCharacteristicUpgradeRequest(0, 0, 20, 15, 0, 140), 71));
            loop = GameNodeProxy.HandleGameNodeSessionAsync(session, server.GetStream(), first, ConnectionProtocol.UriPrefix + "kum");
            Assert.IsType<InventoryWeightEvent>(await Next());
            Assert.IsType<CharacterCharacteristicsEvent>(await Next());
            var loaded = GameSession.SinSocket();
            using (SessionContext.Push(loaded)) Assert.True(DatabaseManager.LoadCharacter(character));
            Assert.Equal((120, 20, 5, 820), (loaded.State.StatStrength, loaded.State.StatVitality,
                loaded.State.StatWisdom, loaded.State.CharacterRemainingPoints));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(clientStream, codec.Encode(new(new ResetCharacterCharacteristicsRequest(), 72)));
            Assert.IsType<InventoryWeightEvent>(await Next());
            Assert.IsType<CharacterCharacteristicsEvent>(await Next());
            using (SessionContext.Push(loaded)) Assert.True(DatabaseManager.LoadCharacter(character));
            Assert.Equal((0, 0, 0, 995), (loaded.State.StatStrength, loaded.State.StatVitality,
                loaded.State.StatWisdom, loaded.State.CharacterRemainingPoints));
            client.Client.Shutdown(SocketShutdown.Send);
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(clientStream.DataAvailable);
        }
        finally
        {
            client.Close();
            GameNodeProxy.Stop();
            listener.Stop();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { DatabaseManager.DeleteCharacter(character, account); }
        }
    }

    [Fact]
    public async Task Typed_targets_repeat_idempotently_replace_previous_allocations_and_reset_without_touching_scrolls()
    {
        var frames = new List<GameFrame>();
        var own = Ready(frames);
        var saved = new List<GameSession>();
        var handler = new CharacteristicsHandler(saved.Add, CharacteristicsMessages.Live);
        var wrong = GameSession.SinSocket();
        wrong.State.StatStrength = 999;
        using var context = SessionContext.Push(wrong);
        using var turn = await own.EnterTurnAsync();
        var codec = new GameCodec();
        var entry = new GameEntryService(own, new GameEntryBackend());
        async Task Send(GameRequest request)
            => Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(own,
                GameNodeProxy.DecodeRequest(own, codec.Encode(new(request, 71))), entry, handler));

        var target = new CharacterCharacteristicUpgradeRequest(0, 0, 20, 15, 0, 140);
        await Send(target);
        Assert.Equal((120, 20, 5, 820), (own.State.StatStrength, own.State.StatVitality, own.State.StatWisdom, own.State.CharacterRemainingPoints));
        Assert.True(PresetHandler.Fits(new Dictionary<int, int> { [Stat.Strength] = 120, [Stat.Vitality] = 20, [Stat.Wisdom] = 5 }, 200, 8));
        Assert.Equal(175, CharacteristicRules.ComputeDistributionCost(120, 0, 0, 0, 20, 5, 8));
        await Send(target);
        Assert.Equal(820, own.State.CharacterRemainingPoints);
        await Send(new CharacterCharacteristicUpgradeRequest(0, 0, 10, 0, 0, 0));
        Assert.Equal((0, 10, 0, 985), (own.State.StatStrength, own.State.StatVitality, own.State.StatWisdom, own.State.CharacterRemainingPoints));
        await Send(new ResetCharacterCharacteristicsRequest());
        Assert.Equal((0, 0, 0, 995), (own.State.StatStrength, own.State.StatVitality, own.State.StatWisdom, own.State.CharacterRemainingPoints));
        Assert.Equal(100, own.State.ScrolledStrength);
        Assert.Equal(999, wrong.State.StatStrength);
        Assert.Equal(4, saved.Count);
        Assert.All(saved, s => Assert.Same(own, s));
        Assert.Equal(8, frames.Count);
        for (int i = 0; i < frames.Count; i += 2)
        {
            var weight = Assert.IsType<InventoryWeightEvent>(frames[i].Content);
            var sheet = Assert.IsType<CharacterCharacteristicsEvent>(frames[i + 1].Content).Stats;
            Assert.Equal(Detail(sheet, Stat.Pods).Base + Detail(sheet, Stat.Pods).Equipment + Detail(sheet, Stat.Pods).Temporary, weight.WeightMax);
        }
        Assert.All(frames, f => Assert.Null(f.Uid));
        Assert.Same(wrong, SessionContext.Actual);
    }

    [Fact]
    public async Task Typed_spending_and_reset_refresh_the_owners_placement_fighter()
    {
        const long owner = 8_941_790_007, fightId = 8_941_790_008;
        await using var pipe = await Economy.ClientPipe.OpenAsync(owner, owner, 990_790_007, "Placement");
        pipe.Session.State.CharacterLevel = 200;
        pipe.Session.State.Breed = 8;
        var fight = new Jondo.Unity.World.Fights.FightInstance(fightId, pipe.Session.MapId);
        var active = (System.Collections.Concurrent.ConcurrentDictionary<long, Jondo.Unity.World.Fights.FightInstance>)
            typeof(FightHandler).GetField("_activeFights", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(null)!;
        using (SessionContext.Push(pipe.Session)) fight.AddPlayer(FightHandler.BuildPlayerFighter(fight));
        var fighter = fight.Buscar(owner)!;
        int initialStrength = fighter.Strength;
        pipe.Session.State.IsInFight = true;
        pipe.Session.State.FightId = fightId;
        Assert.True(active.TryAdd(fightId, fight));
        var wrong = GameSession.SinSocket();
        using var context = SessionContext.Push(wrong);
        using var turn = await pipe.Session.EnterTurnAsync();
        try
        {
            var handler = new CharacteristicsHandler(_ => { }, CharacteristicsMessages.Live);
            Assert.True(await handler.SpendAsync(pipe.Session, new(0, 0, 0, 0, 0, 50)));
            Assert.Equal(initialStrength + 50, fighter.Strength);
            Assert.Equal(50, pipe.Session.State.StatStrength);
            Assert.Equal(new[] { "iun", "kub", "jxg" }, (await pipe.Take(3)).Select(frame => frame.Opcode));

            Assert.True(await handler.ResetAsync(pipe.Session));
            Assert.Equal(initialStrength, fighter.Strength);
            Assert.Equal(0, pipe.Session.State.StatStrength);
            Assert.Equal(new[] { "iun", "kub", "jxg" }, (await pipe.Take(3)).Select(frame => frame.Opcode));
            Assert.Same(wrong, SessionContext.Actual);
        }
        finally
        {
            active.TryRemove(fightId, out _);
            fight.CancelPlacementTimer();
            fight.CancelTurnTimer();
        }
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(int.MaxValue, int.MaxValue, 0)]
    [InlineData(0, 0, 996)]
    [InlineData(0, 1, 0)] // A partial wisdom purchase cannot consume capital.
    [InlineData(101, 0, 0)] // Crossing 100 strength costs two, not one.
    public async Task Invalid_or_unaffordable_targets_preserve_the_entire_distribution(int strength, int wisdom, int vitality)
    {
        var frames = new List<GameFrame>();
        var own = Ready(frames);
        own.State.StatStrength = 30;
        own.State.CharacterRemainingPoints = 965;
        var handler = new CharacteristicsHandler(_ => throw new InvalidOperationException("Must not save"), CharacteristicsMessages.Live);
        await handler.SpendAsync(own, new(0, 0, vitality, wisdom, 0, strength));
        Assert.Equal((30, 0, 0, 965, 100), (own.State.StatStrength, own.State.StatWisdom, own.State.StatVitality,
            own.State.CharacterRemainingPoints, own.State.ScrolledStrength));
        Assert.Equal(2, frames.Count);
        var sheet = Assert.IsType<CharacterCharacteristicsEvent>(frames[1].Content).Stats;
        Assert.Equal(30, Detail(sheet, Stat.Strength).Base);
        Assert.Equal(965, Detail(sheet, Stat.RemainingPoints).Base);
    }

    [Theory]
    [InlineData(0)] // Unauthenticated.
    [InlineData(1)] // No selected character.
    [InlineData(2)] // Not in world.
    [InlineData(3)] // In combat.
    public async Task Dispatch_consumes_upgrade_and_reset_without_effects_when_not_allowed(int stage)
    {
        var frames = new List<GameFrame>();
        var codec = new GameCodec();
        var session = new GameSession(codec, bytes => { frames.Add(codec.Decode(bytes)); return Task.CompletedTask; });
        session.State.StatStrength = 41;
        if (stage > 0) session.BindAccount(8_941_790_003, 1);
        if (stage > 1) session.State.CharacterId = 8_941_790_004;
        if (stage > 2) { session.EnterWorld(); session.State.IsInFight = true; }
        var entry = new GameEntryService(session, new GameEntryBackend());
        foreach (var request in new GameRequest[]
                 { new CharacterCharacteristicUpgradeRequest(0, 0, 1, 0, 0, 0), new ResetCharacterCharacteristicsRequest() })
            Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(session,
                GameNodeProxy.DecodeRequest(session, codec.Encode(new(request, 1))), entry));
        Assert.Equal(41, session.State.StatStrength);
        Assert.Empty(frames);
    }

    [Fact]
    public async Task Malformed_owned_requests_and_event_envelopes_never_reach_the_service()
    {
        var frames = new List<GameFrame>();
        var own = Ready(frames);
        var handler = new CharacteristicsHandler(_ => throw new InvalidOperationException("Must not save"), CharacteristicsMessages.Live);
        byte[] malformed = Pb.New().Msg(2, Pb.New().Msg(1, Pb.New().Str(1, ConnectionProtocol.UriPrefix + "kum")
            .Bytes(2, [0x08, 0x80])).Var(2, 1)).Build();
        var entry = new GameEntryService(own, new GameEntryBackend());
        foreach (var payload in new[] { malformed, ConnectionProtocol.Push("kum", Pb.New().Var(3, 5).Build()) })
            Assert.Equal(GameEntryResult.Unhandled, await GameNodeProxy.DispatchTypedAsync(own,
                GameNodeProxy.DecodeRequest(own, payload), entry, handler));
        byte[] unknown = Pb.New().Msg(2, Pb.New().Msg(1, Pb.New().Str(1, ConnectionProtocol.UriPrefix + "kum-extra")
            .Bytes(2, [])).Var(2, 1)).Build();
        Assert.Null(GameNodeProxy.DecodeRequest(own, unknown));
        Assert.Equal(995, own.State.CharacterRemainingPoints);
        Assert.Empty(frames);
    }

    [Fact]
    public async Task Persistence_and_transport_failures_propagate_and_stop_following_notifications()
    {
        var frames = new List<GameFrame>();
        var own = Ready(frames);
        var failure = new IOException("save failed");
        var handler = new CharacteristicsHandler(_ => throw failure, CharacteristicsMessages.Live);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => handler.ResetAsync(own)));
        Assert.Empty(frames);

        var codec = new GameCodec();
        var outgoing = new List<GameFrame>();
        var broken = new GameSession(codec, bytes => { outgoing.Add(codec.Decode(bytes)); throw new IOException("transport failed"); });
        broken.BindAccount(8_941_790_005, 1);
        broken.State.CharacterId = 8_941_790_006;
        broken.EnterWorld();
        await Assert.ThrowsAsync<IOException>(() => new CharacteristicsHandler(_ => { }, CharacteristicsMessages.Live).ResetAsync(broken));
        Assert.IsType<InventoryWeightEvent>(Assert.Single(outgoing).Content);
    }
}
