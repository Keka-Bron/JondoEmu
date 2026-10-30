using System.Net;
using System.Net.Sockets;
using Jondo.Unity.Protocol.Connection;
using Jondo.Unity.Protocol.Connection.Wire;
using Jondo.Unity.Protocol.Connection.Wire.Messages;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server.Network.Connection;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ConnectionTests
{
    private static readonly ConnectionCodec Codec = new();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Fragmented_identification_and_coalesced_requests_end_with_redirect_and_EOF()
    {
        var backend = new Backend();
        await using var socket = await Loopback.Start(backend);
        byte[] auth = Frame(new ConnectionRequest("auth-id", new IdentificationRequest("device", new TokenIdentification("token-a", null))));
        foreach (byte value in auth) await socket.Stream.WriteAsync(new[] { value });
        var accepted = Assert.IsType<ConnectionResponse>(await socket.Read());
        Assert.Equal("auth-id", accepted.CorrelationId);
        Assert.Equal(101, Assert.IsType<IdentificationSuccess>(Assert.IsType<IdentificationResponse>(accepted.Content).Result).AccountId);

        byte[] burst = Frame(new ConnectionRequest("ping-id", new Ping()))
            .Concat(Frame(new ConnectionRequest("ignored-id", new ReleaseAccountRequest())))
            .Concat(Frame(new ConnectionRequest("selection-id", new SelectServerRequest(290)))).ToArray();
        await socket.Stream.WriteAsync(burst);
        Assert.Equal(new ConnectionResponse("ping-id", new Pong()), await socket.Read());
        var selected = Assert.IsType<ConnectionResponse>(await socket.Read());
        Assert.Equal("selection-id", selected.CorrelationId);
        var redirect = Assert.IsType<SelectServerSuccess>(Assert.IsType<SelectServerResponse>(selected.Content).Result);
        Assert.Equal(new[] { 5555, 5555 }, redirect.Ports);
        Assert.Equal("127.0.0.1", redirect.Host);
        Assert.Equal(0, await socket.Stream.ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout));
        await socket.Completion.WaitAsync(Timeout);
        var ticket = SessionRegistry.Redeem(redirect.Ticket);
        Assert.NotNull(ticket);
        Assert.Equal(101, ticket.AccountId);
        Assert.Equal(290, ticket.ServerId);
        Assert.Null(SessionRegistry.Redeem(redirect.Ticket));
        Assert.Equal(1, backend.Tickets);
    }

    [Theory]
    [InlineData("invalid-token")]
    [InlineData("selection-first")]
    [InlineData("closed-server")]
    [InlineData("malformed")]
    public async Task Rejected_connections_close_without_issuing_tickets(string scenario)
    {
        var backend = new Backend();
        await using var socket = await Loopback.Start(backend);
        if (scenario == "closed-server")
        {
            await socket.Stream.WriteAsync(Frame(new ConnectionRequest("a", new IdentificationRequest("", new TokenIdentification("token-a", null)))));
            await socket.Read();
        }
        byte[] request = scenario switch
        {
            "invalid-token" => Frame(new ConnectionRequest("a", new IdentificationRequest("", new TokenIdentification("wrong", null)))),
            "selection-first" => Frame(new ConnectionRequest("s", new SelectServerRequest(290))),
            "closed-server" => Frame(new ConnectionRequest("s", new SelectServerRequest(291))),
            _ => new byte[] { 1, 0xff }
        };
        await socket.Stream.WriteAsync(request);
        Assert.Equal(0, await socket.Stream.ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout));
        await socket.Completion.WaitAsync(Timeout);
        Assert.Equal(0, backend.Tickets);
    }

    [Fact]
    public async Task Two_socket_sessions_keep_their_account_and_ticket_bindings()
    {
        var backend = new Backend();
        await using var first = await Loopback.Start(backend);
        await using var second = await Loopback.Start(backend);
        async Task<long> Login(Loopback socket, string token)
        {
            await socket.Stream.WriteAsync(Frame(new ConnectionRequest("id", new IdentificationRequest("", new TokenIdentification(token, null)))));
            await socket.Read();
            await socket.Stream.WriteAsync(Frame(new ConnectionRequest("next", new SelectServerRequest(290))));
            var response = Assert.IsType<ConnectionResponse>(await socket.Read());
            var success = Assert.IsType<SelectServerSuccess>(Assert.IsType<SelectServerResponse>(response.Content).Result);
            return SessionRegistry.Redeem(success.Ticket)!.AccountId;
        }
        Assert.Equal(new[] { 101L, 102L }, await Task.WhenAll(Login(first, "token-a"), Login(second, "token-b")));
    }

    [Fact]
    public async Task Backend_failures_are_not_reported_as_protobuf_decode_failures()
    {
        var logs = new List<string>();
        var service = new ConnectionService(new Backend { FailIdentification = true });
        using var stream = new MemoryStream();
        byte[] request = Codec.Encode(new ConnectionRequest("id", new IdentificationRequest("", new TokenIdentification("token-a", null))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectionTransport.RunAsync(stream, request,
            service, Codec, () => true, logs.Add));
        Assert.Equal("Storage failure", error.Message);
        Assert.Empty(logs);
    }

    [Fact]
    public void Production_ticket_backend_uses_launch_language_and_single_use_registry()
    {
        const long account = 9_000_000_101;
        string token = Guid.NewGuid().ToString("N");
        var launch = ClientLaunchRegistry.Register(account, token, Guid.NewGuid().ToString("N"), "fr", "connection-test");
        try
        {
            var backend = new ConnectionBackend();
            Assert.Equal(account, backend.ResolveAccount(token));
            var result = backend.IssueTicket(account, 290);
            var ticket = SessionRegistry.Redeem(result.Ticket);
            Assert.NotNull(ticket);
            Assert.Equal("fr", ticket.Language);
            Assert.Equal(account, ticket.AccountId);
            Assert.Equal(290, ticket.ServerId);
            Assert.Null(SessionRegistry.Redeem(result.Ticket));
        }
        finally { ClientLaunchRegistry.Remove(launch); }
    }

    [Fact]
    public void Account_projection_keeps_slot_categories_zero_based_breeds_dates_and_empty_rights()
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.FromHours(2));
        var list = ConnectionAccountProjection.ServerList(
            new[] { new DatabaseManager.DbServer { Id = 290, Type = 1, Joinable = true }, new DatabaseManager.DbServer { Id = 291, Type = 0 } },
            new[] { new DatabaseManager.DbCharacter { AccountId = 7, ServerId = 290, Name = "Dummy", Breed = 1, Sex = 0, Level = 1, LastConnection = "" },
                new DatabaseManager.DbCharacter { AccountId = 7, ServerId = 291, Name = "Other", Breed = 19, Sex = 1, Level = 201, LastConnection = "2026-08-01T12:00:00.000+02:00" } }, now);
        var response = new ConnectionResponse("0", new IdentificationResponse(new IdentificationSuccess(7, "Dummy", "0007", list,
            "2027-08-11T12:00:00+02:00", new AccountRights(false, false, false, false), null, null, null)));
        byte[] encoded = Codec.Encode(response);
        var accepted = mhh.Parser.ParseFrom(encoded).Gfce.Gfcy.Gffu;
        Assert.Equal(7, accepted.Gfez);
        Assert.NotNull(accepted.Gffe);
        Assert.Equal(Enumerable.Range(0, 7), accepted.Gffc.Gfgx.Select(s => (int)s.Gfgr));
        Assert.All(accepted.Gffc.Gfgx, s => Assert.Equal(5, s.Gfgs));
        Assert.Equal(0, (int)accepted.Gffc.Gfgw[0].Gfhe[0].Gfin);
        Assert.Equal(18, (int)accepted.Gffc.Gfgw[1].Gfhe[0].Gfin);
        Assert.Equal("2026-08-11T12:00:00.000+02:00", accepted.Gffc.Gfgw[0].Gfhe[0].Gfiq);
        Assert.Equal("2026-08-01T12:00:00.000+02:00", accepted.Gffc.Gfgw[1].Gfhe[0].Gfiq);

        // Independent, pre-migration field-number construction. This pins the old
        // reply bytes while exercising the new projection and generated conversions.
        var servers = Pb.New()
            .Msg(1, Pb.New().Msg(1, Pb.New().Var(1, 290).Var(3, 1))
                .Msg(3, Pb.New().Str(1, "Dummy").Var(4, 1).Str(5, "2026-08-11T12:00:00.000+02:00")))
            .Msg(1, Pb.New().Msg(1, Pb.New().Var(1, 291))
                .Msg(3, Pb.New().Str(1, "Other").Var(2, 18).Var(3, 1).Var(4, 201).Str(5, "2026-08-01T12:00:00.000+02:00")));
        for (int category = 0; category < 7; category++) servers.Msg(2, Pb.New().VarIfNotZero(1, category).Var(2, 5));
        byte[] legacy = Pb.New().Msg(2, Pb.New().Str(1, "0").Msg(3, Pb.New().Msg(1,
            Pb.New().Var(1, 7).Str(2, "Dummy").Str(3, "0007").Msg(4, servers).Str(5, "2027-08-11T12:00:00+02:00").EmptyMsg(6)))).Build();
        Assert.Equal(legacy, encoded);
    }

    [Fact]
    public void Database_categories_keep_their_semantics_and_wire_values_for_servers_and_slots()
    {
        (int Value, ServerCategory Category)[] expected =
        [
            (0, ServerCategory.MonoAccount), (1, ServerCategory.Classic),
            (2, ServerCategory.PioneerMonoAccount), (3, ServerCategory.PioneerClassic),
            (4, ServerCategory.Tournament), (5, ServerCategory.Epic), (6, ServerCategory.Temporis)
        ];
        var list = ConnectionAccountProjection.ServerList(
            expected.Select(row => new DatabaseManager.DbServer { Id = 100 + row.Value, Type = row.Value }).ToArray(),
            [], DateTimeOffset.UnixEpoch);
        Assert.Equal(expected.Select(row => row.Category), list.Servers.Select(server => server.Server!.Category));
        Assert.Equal(expected.Select(row => row.Category), list.Slots.Select(slot => slot.Category));

        var response = new ConnectionResponse("categories", new IdentificationResponse(new IdentificationSuccess(
            7, "Dummy", "0007", list, "2027-01-01T00:00:00+00:00", null, null, null, null)));
        var wire = mhh.Parser.ParseFrom(Codec.Encode(response)).Gfce.Gfcy.Gffu.Gffc;
        Assert.Equal(expected.Select(row => row.Value), wire.Gfgw.Select(server => (int)server.Gfhc.Gfhk));
        Assert.Equal(expected.Select(row => row.Value), wire.Gfgx.Select(slot => (int)slot.Gfgr));
        Assert.All(wire.Gfgx, slot => Assert.Equal(5, slot.Gfgs));
    }

    private sealed class Backend : IConnectionBackend
    {
        public int Tickets;
        public bool FailIdentification;
        public long ResolveAccount(string token) => token switch { "token-a" => 101, "token-b" => 102, _ => 0 };
        public IdentificationSuccess Identify(long id) => FailIdentification ? throw new InvalidOperationException("Storage failure")
            : new(id, "dummy", "0001", new ServerList([], []), "2027-01-01T00:00:00+00:00", new(false, false, false, false), null, null, null);
        public bool IsServerJoinable(int id) => id == 290;
        public SelectServerSuccess IssueTicket(long account, int server)
        {
            Interlocked.Increment(ref Tickets);
            return new(SessionRegistry.Issue(account, server).Value, "127.0.0.1", new[] { 5555, 5555 });
        }
    }

    private static byte[] Frame(ConnectionMessage message)
    {
        byte[] payload = Codec.Encode(message);
        var prefix = new List<byte>();
        uint length = (uint)payload.Length;
        while (length >= 128) { prefix.Add((byte)((length & 127) | 128)); length >>= 7; }
        prefix.Add((byte)length);
        return prefix.Concat(payload).ToArray();
    }

    private sealed class Loopback(TcpClient client, Task completion) : IAsyncDisposable
    {
        public NetworkStream Stream => client.GetStream();
        public Task Completion => completion;
        public static async Task<Loopback> Start(IConnectionBackend backend)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var completion = Task.Run(async () =>
            {
                try
                {
                    using var peer = await listener.AcceptTcpClientAsync();
                    var stream = peer.GetStream();
                    byte[]? first = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
                    if (first != null) await ConnectionTransport.RunAsync(stream, first, new ConnectionService(backend), Codec, () => true, _ => { });
                }
                finally { listener.Stop(); }
            });
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            return new(client, completion);
        }
        public async Task<ConnectionMessage> Read()
        {
            byte[]? payload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(Stream).WaitAsync(Timeout);
            Assert.NotNull(payload);
            return Codec.Decode(payload);
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await completion.WaitAsync(Timeout);
        }
    }
}
