using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// A session with a real socket behind it, and the client's end to read what the server sends
    /// -- the way BankVisitTests walks the bank capture -- for the storage and house tests.
    /// </summary>
    internal sealed class ClientPipe : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _client;
        private readonly TcpClient _server;

        public GameSession Session { get; }
        public NetworkStream ToClient { get; }
        public NetworkStream FromServer { get; }

        private ClientPipe(TcpListener listener, TcpClient client, TcpClient server)
        {
            _listener = listener;
            _client = client;
            _server = server;
            ToClient = server.GetStream();
            FromServer = client.GetStream();
            Session = new GameSession(ToClient);
        }

        public static async Task<ClientPipe> OpenAsync(long accountId, long characterId, long mapId, string name = "")
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await listener.AcceptTcpClientAsync();

            var pipe = new ClientPipe(listener, client, server);
            pipe.Session.BindAccount(accountId, 1);
            pipe.Session.State.CharacterId = characterId;
            pipe.Session.State.CharacterName = name;
            pipe.Session.State.MapId = mapId;
            pipe.Session.EnterWorld();
            Assert.True(SessionRegistry.Register(pipe.Session));
            return pipe;
        }

        /// <summary>The next frame, which has to be that opcode: its payload.</summary>
        public async Task<byte[]> Next(string opcode)
        {
            var (got, payload) = await NextAny();
            Assert.True(got == opcode, $"{opcode} was expected and {got} came");
            return payload;
        }

        /// <summary>The next frame, whatever it is: its opcode and payload.</summary>
        public async Task<(string Opcode, byte[] Payload)> NextAny()
        {
            var read = Jondo.Protocol.NetworkMessage.ReadFrameAsync(FromServer);
            var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(first == read, "nothing arrived");

            byte[] frame = await read;
            Assert.NotNull(frame);
            string opcode = ConnectionProtocol.ReadOpcode(frame) ?? "";
            return (opcode, ConnectionProtocol.ReadPayload(frame, opcode) ?? Array.Empty<byte>());
        }

        /// <summary>The opcodes of the next <paramref name="count"/> frames.</summary>
        public async Task<List<(string Opcode, byte[] Payload)>> Take(int count)
        {
            var frames = new List<(string, byte[])>();
            for (int i = 0; i < count; i++) frames.Add(await NextAny());
            return frames;
        }

        /// <summary>Nothing more has arrived within a short while.</summary>
        public async Task Quiet()
        {
            var read = Jondo.Protocol.NetworkMessage.ReadFrameAsync(FromServer);
            var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(300)));
            Assert.False(first == read, "a frame arrived where nothing was expected");
        }

        public ValueTask DisposeAsync()
        {
            SessionRegistry.Unregister(Session);
            _client.Dispose();
            _server.Dispose();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
