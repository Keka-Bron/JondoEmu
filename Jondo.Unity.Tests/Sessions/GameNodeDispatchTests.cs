using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions;

// These tests own the process-wide listener and temporarily capture Console.Out.
[CollectionDefinition("game-node-listener", DisableParallelization = true)]
public sealed class GameNodeListenerCollection;

[Collection("game-node-listener")]
public sealed class GameNodeDispatchTests
{
    private static readonly GameCodec Wire = new();

    [Theory]
    [InlineData("handled")]
    [InlineData("unhandled")]
    [InlineData("decode-failure")]
    public async Task Raw_routing_only_runs_without_a_typed_handler(string scenario)
    {
        var codec = new AliasCodec(scenario == "decode-failure",
            scenario == "unhandled" ? new BakApiTokenRequest() : new PingRequest(false));
        await using var pipe = await Pipe.OpenAsync(codec);
        // Both routes have observable replies for this alias. Typed handling must claim it;
        // an unsupported typed request or failed decode must retain the legacy reply.
        byte[] first = Request("kod", []);
        pipe.Start(first);
        if (scenario == "handled")
            Assert.Equal(new PongEvent(false), Assert.IsType<PongEvent>(Wire.Decode(await pipe.Next()).Content));
        else
            Assert.Equal(ConnectionProtocol.UriPrefix + "kns", Wire.ReadEnvelope(await pipe.Next()).Content.TypeUrl);
        await pipe.Finish();
        Assert.Equal(1, codec.Decodes);
        Assert.Equal(scenario == "handled" ? 1 : 0, codec.Encodes);
        Assert.False(pipe.ClientStream.DataAvailable);
    }

    [Theory]
    [InlineData("movement")]
    [InlineData("cancel")]
    [InlineData("confirm")]
    [InlineData("map-change")]
    [InlineData("upgrade")]
    [InlineData("reset")]
    [InlineData("weekly-reset")]
    public async Task Refused_typed_requests_skip_raw_routing_and_allow_the_next_packet(string kind)
    {
        GameRequest request = kind switch
        {
            "movement" => new MapMovementRequest(42, [1, 2], false),
            "cancel" => new MapMovementCancelRequest(1),
            "confirm" => new MapMovementConfirmRequest(),
            "map-change" => new MapChangeRequest(43, false),
            "upgrade" => new CharacterCharacteristicUpgradeRequest(0, 0, 1, 0, 0, 0),
            "reset" => new ResetCharacterCharacteristicsRequest(),
            _ => new GuildWeeklyResetDateRequest(),
        };
        var codec = new AliasCodec(request: request);
        await using var pipe = await Pipe.OpenAsync(codec); // Authenticated, but no selected character.
        pipe.Start(Request("kod", []));
        await pipe.Send(Request("dispatch-barrier", []));
        Assert.IsType<PongEvent>(Wire.Decode(await pipe.Next()).Content);
        await pipe.Finish();
        Assert.Equal(2, codec.Decodes);
        Assert.Equal(1, codec.Encodes);
        Assert.False(pipe.ClientStream.DataAvailable);
    }

    [Fact]
    public async Task Typed_close_skips_raw_routing()
    {
        // Re-identification closes the authenticated session before the legacy alias can reply.
        var codec = new AliasCodec(request: new IdentificationRequest("unused", "en"));
        await using var pipe = await Pipe.OpenAsync(codec);
        pipe.Start(Request("kod", []));
        await pipe.Finish();
        Assert.Equal(1, codec.Decodes);
        Assert.Equal(0, codec.Encodes);
        Assert.False(pipe.ClientStream.DataAvailable);
    }

    [Fact]
    public async Task Retired_map_and_transition_aliases_only_reach_diagnostics_and_leave_the_connection_usable()
    {
        string[] retired = ["kkr", "jqf", "igx", "joi", "jos", "jpp", "loy", "lpj", "hmv", "ibt"];
        var before = retired.ToDictionary(alias => alias, Recorded);
        var codec = new CountingCodec();
        await using var pipe = await Pipe.OpenAsync(codec);
        pipe.Session.State.MapId = 91_000_910;
        pipe.Session.State.CellId = 123;
        pipe.Session.State.Orientation = 5;
        byte[] payload = Pb.New().Var(1, 91_000_911).Packed(2, new long[] { 245, 246 }).Build();
        pipe.Start(Request(retired[0], payload));
        foreach (string alias in retired.Skip(1)) await pipe.Send(Request(alias, payload));
        // A current request is a barrier after the retired packets. Any old
        // compatibility response would arrive before this typed reply.
        await pipe.Send(Wire.Encode(new(new PingRequest(false), int.MinValue)));
        Assert.IsType<PongEvent>(Wire.Decode(await pipe.Next()).Content);
        await pipe.Finish();
        Assert.False(pipe.ClientStream.DataAvailable);
        Assert.Equal(91_000_910, pipe.Session.MapId);
        Assert.Equal(123, pipe.Session.State.CellId);
        Assert.Equal(5, pipe.Session.State.Orientation);
        Assert.False(pipe.Session.State.IsInFight);
        Assert.False(pipe.Session.IsInWorld);
        Assert.Equal(retired.Length + 1, codec.Decodes);
        Assert.Equal(1, codec.Encodes);
        foreach (string alias in retired) Assert.Equal(before[alias] + 1, Recorded(alias));
    }

    [Fact]
    public async Task Only_requests_without_typed_handlers_reach_raw_diagnostics_on_each_packet()
    {
        var codec = new CountingCodec();
        await using var pipe = await Pipe.OpenAsync(codec);
        byte[] ping = Wire.Encode(new(new PingRequest(false), 1));
        byte[] unhandled = Wire.Encode(new(new BakApiTokenRequest(), 2));
        string pingAlias = Wire.ReadEnvelope(ping).Alias;
        string unhandledAlias = Wire.ReadEnvelope(unhandled).Alias;
        long pingBefore = Recorded(pingAlias), unhandledBefore = Recorded(unhandledAlias);
        var original = Console.Out;
        using var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            pipe.Start(ping);
            Assert.IsType<PongEvent>(Wire.Decode(await pipe.Next()).Content);
            await pipe.Send(unhandled); // Decoded, but no typed or raw handler.
            await pipe.Send(Request(pingAlias, [0x08, 0x80])); // Invalid mapped payload still falls through.
            await pipe.Send(Request("unmapped-proxy-test", []));
            await pipe.Send(ping); // A barrier after the unhandled packets.
            Assert.IsType<PongEvent>(Wire.Decode(await pipe.Next()).Content);
            await pipe.Finish();
        }
        finally { Console.SetOut(original); }

        // Missing mappings fall back silently; the malformed mapped Ping still gets its decode diagnostic.
        string[] decodeDiagnostics = capture.ToString().Split('\n')
            .Where(line => line.Contains("[Game Node] Typed request decode unavailable:", StringComparison.Ordinal)).ToArray();
        Assert.Single(decodeDiagnostics);
        Assert.DoesNotContain("Unknown mapped payload", capture.ToString(), StringComparison.Ordinal);
        string[] warnings = capture.ToString().Split('\n')
            .Where(line => line.Contains("UNHANDLED CLIENT PACKET DETECTED:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, warnings.Length);
        Assert.Single(warnings, line => line.Contains(ConnectionProtocol.UriPrefix + unhandledAlias, StringComparison.Ordinal));
        Assert.Single(warnings, line => line.Contains(ConnectionProtocol.UriPrefix + pingAlias, StringComparison.Ordinal));
        Assert.Single(warnings, line => line.Contains(ConnectionProtocol.UriPrefix + "unmapped-proxy-test", StringComparison.Ordinal));
        Assert.Equal(pingBefore + 1, Recorded(pingAlias)); // Only the malformed Ping, not either handled Ping.
        Assert.Equal(unhandledBefore + 1, Recorded(unhandledAlias));
        Assert.Equal(5, codec.Decodes);
        Assert.Equal(2, codec.Encodes);
        Assert.False(pipe.ClientStream.DataAvailable);
    }

    private static long Recorded(string alias) => UnknownPackets.Top(1000)
        .Where(row => row.Opcode == alias && row.Kind == UnknownPackets.Kind.Unhandled).Sum(row => row.Occurrences);

    private static byte[] Request(string alias, byte[] body) => Pb.New().Msg(2, Pb.New()
        .Msg(1, Pb.New().Str(1, ConnectionProtocol.UriPrefix + alias).Bytes(2, body)).Var(2, 7)).Build();

    private class CountingCodec : IGameCodec
    {
        internal int Decodes, Encodes;
        public virtual GameFrame Decode(ReadOnlySpan<byte> payload) { Decodes++; return Wire.Decode(payload); }
        public byte[] Encode(GameFrame frame) { Encodes++; return Wire.Encode(frame); }
    }

    private sealed class AliasCodec(bool failDecode = false, GameRequest? request = null) : CountingCodec
    {
        public override GameFrame Decode(ReadOnlySpan<byte> payload)
        {
            Decodes++;
            if (failDecode) throw new GameConversionException("Unsupported injected mapping");
            var envelope = Wire.ReadEnvelope(payload);
            return new(envelope.Alias == "dispatch-barrier" ? new PingRequest(false) : request ?? new PingRequest(false),
                envelope.Uid);
        }
    }

    private sealed class Pipe(TcpListener listener, TcpClient client, TcpClient server, IGameCodec codec) : IAsyncDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private Task loop = Task.CompletedTask;
        internal NetworkStream ClientStream { get; } = client.GetStream();
        internal GameSession Session { get; } = new(server.GetStream(), codec);

        internal static async Task<Pipe> OpenAsync(IGameCodec codec)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port).WaitAsync(Timeout);
                var server = await listener.AcceptTcpClientAsync().WaitAsync(Timeout);
                var pipe = new Pipe(listener, client, server, codec);
                pipe.Session.BindAccount(9_100_870_101, 1);
                return pipe;
            }
            catch { client.Dispose(); listener.Stop(); throw; }
        }

        internal void Start(byte[] first)
        {
            GameNodeProxy.Start(0);
            loop = GameNodeProxy.HandleGameNodeSessionAsync(Session, server.GetStream(), first, "");
        }
        internal Task<byte[]> Next() => Jondo.Protocol.NetworkMessage.ReadFrameAsync(ClientStream).WaitAsync(Timeout);
        internal Task Send(byte[] packet) => Jondo.Protocol.NetworkMessage.WriteFrameAsync(ClientStream, packet).WaitAsync(Timeout);
        internal async Task Finish()
        {
            client.Client.Shutdown(SocketShutdown.Send);
            await loop.WaitAsync(Timeout);
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            try { await loop.WaitAsync(Timeout); }
            finally { GameNodeProxy.Stop(); server.Dispose(); listener.Stop(); }
        }
    }
}
