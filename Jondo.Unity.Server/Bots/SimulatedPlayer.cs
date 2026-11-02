using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Connection;
using Jondo.Unity.Protocol.Connection.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// One simulated player: a client without a screen. It comes in as the client does and then
    /// lives in the world over its own socket (see <see cref="SimulatedPlayers"/>).
    /// </summary>
    /// <remarks>
    /// The way in is the one the captures show -- «desde eleccion servidor a eleccion personaje y
    /// carga en world» and the successful creation --: the token to the connection server, the
    /// server picked and its ticket, then on the game socket kqz, the list, the creation (kvz) the
    /// first time, the selection (kvw), lqc, and kmv with jrh for every map it is put on. Every
    /// client message goes as the client wraps it, { f2 { f1 Any, f2 -1 } }.
    ///
    /// Its eyes are the server's state: its session, the monsters and resources of its map, its
    /// fight's board. Its hands are only the socket.
    /// </remarks>
    internal sealed partial class SimulatedPlayer
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        private readonly int _number;
        private readonly long _accountId;
        private readonly string _name;
        private readonly Random _dice;
        private readonly SemaphoreSlim _writing = new(1, 1);

        private TcpClient? _tcp;
        private NetworkStream? _stream;
        private long _characterId;
        private DateTime _leaveAt = DateTime.MaxValue;
        private volatile bool _leaving;
        private volatile bool _closed;

        // What the reader hands to whoever is waiting for it.
        private TaskCompletionSource<List<(long Id, string Name)>>? _list;
        private TaskCompletionSource<int>? _created;
        private TaskCompletionSource<bool>? _selected;
        private TaskCompletionSource<long>? _arrived;
        private volatile bool _fightMapComing;

        public SimulatedPlayer(int number, long accountId)
        {
            _number = number;
            _accountId = accountId;
            _name = SimulatedPlayers.NameOf(number);
            _dice = new Random(Guid.NewGuid().GetHashCode());
        }

        /// <summary>Its connection is over: it left, or was dropped.</summary>
        public bool Finished { get; private set; }

        /// <summary>It is on its way out: it leaves once it is not fighting.</summary>
        public bool Leaving => _leaving;

        /// <summary>Its character's session on the server, as long as it is in.</summary>
        private GameSession? Session => _characterId == 0 ? null : SessionRegistry.FindByCharacter(_characterId);

        /// <summary>Leaves as soon as it is free.</summary>
        public void LeaveSoon() => _leaving = true;

        /// <summary>Its life: in, a while in the world, out.</summary>
        public async Task RunAsync(CancellationToken stop)
        {
            try
            {
                string? ticket = await TicketAsync(stop);
                if (ticket == null) return;

                _tcp = new TcpClient { NoDelay = true };
                await _tcp.ConnectAsync(IPAddress.Loopback, Program.gamePort, stop);
                _stream = _tcp.GetStream();
                _ = Task.Run(() => ReadAsync(stop));

                if (!await EnterAsync(ticket, stop)) return;
                // A session as players have them: a quarter of an hour to an hour and a half.
                _leaveAt = DateTime.UtcNow + TimeSpan.FromMinutes(_dice.Next(15, 91));
                Console.WriteLine($"[Bots] {_name} is in the world.");
                _ = Task.Run(() => HeartbeatAsync(stop));
                await LiveAsync(stop);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is SocketException or System.IO.IOException or ObjectDisposedException)
            {
                Program.LogDebug($"[Bots] {_name} lost its connection: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bots] {_name} stopped: {ex.Message}");
            }
            finally
            {
                Disconnect();
                Finished = true;
            }
        }

        /// <summary>Closes its socket; the server saves and lets the character go, as for anybody.</summary>
        public void Disconnect()
        {
            _closed = true;
            try { _tcp?.Close(); } catch { }
        }

        // ─── The way in ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The connection server's half: a token the launcher would have given, the identification,
        /// the server picked, and the ticket that opens the game socket.
        /// </summary>
        private async Task<string?> TicketAsync(CancellationToken stop)
        {
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            ClientLaunchRegistry.RegisterToken(_accountId, token);
            _token = token;

            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, Program.gamePort, stop);
            var stream = tcp.GetStream();
            var codec = new ConnectionCodec();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, codec.Encode(new ConnectionRequest("1",
                new IdentificationRequest(_name, new TokenIdentification(token, null)))));
            if (await ReplyAsync(stream, codec) is not IdentificationResponse { Result: IdentificationSuccess })
            {
                Console.WriteLine($"[Bots] {_name} was not identified.");
                return null;
            }

            int server = ServerToJoin();
            if (server == 0) return null;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, codec.Encode(new ConnectionRequest("2",
                new SelectServerRequest(server))));
            return await ReplyAsync(stream, codec) is SelectServerResponse { Result: SelectServerSuccess selected }
                ? selected.Ticket
                : null;
        }

        /// <summary>The server players are put on: the default one if it takes players, or the first that does.</summary>
        private static int ServerToJoin()
        {
            if (DatabaseManager.IsServerJoinable(DatabaseManager.DefaultServerId)) return DatabaseManager.DefaultServerId;
            return DatabaseManager.GetServers().Select(s => s.Id).FirstOrDefault(DatabaseManager.IsServerJoinable);
        }

        private static async Task<ResponseContent?> ReplyAsync(NetworkStream stream, ConnectionCodec codec)
        {
            var read = Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
            if (await Task.WhenAny(read, Task.Delay(Patience)) != read) return null;
            byte[] frame = await read;
            if (frame == null) return null;
            return codec.Decode(frame) is ConnectionResponse response ? response.Content : null;
        }

        /// <summary>
        /// The game socket's half: the ticket, the list, the character -- made the first time, of a
        /// class drawn at random --, its selection and the first map.
        /// </summary>
        private async Task<bool> EnterAsync(string ticket, CancellationToken stop)
        {
            _list = new TaskCompletionSource<List<(long, string)>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _selected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendAsync(Op.Kqz, Pb.New().Str(2, ticket).Str(3, "es").Build());
            await SendAsync("krt");

            var characters = await WithinAsync(_list.Task);
            if (characters == null) return false;
            var mine = characters.FirstOrDefault(c => c.Name == _name);
            bool born = false;
            if (mine.Id == 0)
            {
                _list = new TaskCompletionSource<List<(long, string)>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _created = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                await SendAsync(Op.Kvz, Creation());
                int refused = await WithinAsync(_created.Task, -1);
                if (refused != 0)
                {
                    Console.WriteLine($"[Bots] {_name} could not be created (reason {refused}).");
                    return false;
                }
                characters = await WithinAsync(_list.Task);
                mine = characters?.FirstOrDefault(c => c.Name == _name) ?? default;
                if (mine.Id == 0) return false;
                born = true;
            }
            _characterId = mine.Id;

            // Born at a level drawn from the whole range, so that there is somebody for every
            // Koliseo bracket; the capital it brings is spent in the world, as a player does.
            int level = DatabaseManager.GetCharacterById(_characterId)?.Level ?? 1;
            if (born)
            {
                level = _dice.Next(1, 201);
                DatabaseManager.SetLevelOffline(_characterId, level);
                Console.WriteLine($"[Bots] {_name} is born at level {level}.");
            }
            JoinTheWar(level);

            // A character still in a fight is not selected: the kvd's «go on» (kwb) put it back already.
            if (!_selected.Task.IsCompleted) await SendAsync(Op.Kvw, Pb.New().Var(1, _characterId).Build());
            if (!await WithinAsync(_selected.Task)) return false;
            await SendAsync(Op.Lqc, Pb.New().Var(1, 1).Build());

            for (int waited = 0; waited < 300; waited++)
            {
                if (Session?.IsInWorld == true) break;
                await Task.Delay(100, stop);
            }
            if (Session?.IsInWorld != true) return false;

            await SpendCapitalAsync();
            await WingsAsync();
            return true;
        }

        /// <summary>
        /// The kvz of its creation, as the client's: { f1 { f1 name, f2 head, f3 colours, f6 sex,
        /// f7 class } }. A class at random, either sex, one of the class's creation heads, and each
        /// of the five colours its class's own or one of its own.
        /// </summary>
        private byte[] Creation()
        {
            var classes = SpellTable.ClassBreeds;
            int breed = classes.Count > 0 ? classes[_dice.Next(classes.Count)] : 8;
            int sex = _dice.Next(2);
            var heads = HeadTable.OfferedAtCreation(breed, sex);
            int head = heads.Count > 0 ? heads[_dice.Next(heads.Count)] : HeadTable.DefaultHeadId(breed, sex);
            var colors = Enumerable.Range(0, 5)
                .Select(_ => _dice.Next(2) == 0 ? -1L : _dice.Next(0x1000000))
                .ToList();
            return Pb.New().Msg(1, Pb.New()
                .Str(1, _name)
                .Var(2, head)
                .Packed(3, colors)
                .Var(6, sex)
                .Var(7, breed)).Build();
        }

        /// <summary>The kvi's characters: { f1 { f1 { f2 name … }, f2 id } } each.</summary>
        private static List<(long Id, string Name)> ReadCharacters(byte[] kvi)
        {
            var list = new List<(long, string)>();
            foreach (var entry in ProtoMessage.Parse(kvi).Fields.Where(f => f.FieldNumber == 1 && f.WireType == 2))
            {
                var fields = ProtoMessage.Parse(entry.BytesValue).Fields;
                long id = fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0;
                var details = fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
                string name = details == null ? "" : ProtoMessage.Parse(details.BytesValue).Fields
                    .Where(f => f.FieldNumber == 2 && f.WireType == 2)
                    .Select(f => System.Text.Encoding.UTF8.GetString(f.BytesValue)).FirstOrDefault() ?? "";
                list.Add((id, name));
            }
            return list;
        }

        // ─── The socket ──────────────────────────────────────────────────────────────────────

        /// <summary>A message of the client's, wrapped as the client wraps it.</summary>
        private async Task SendAsync(string op, byte[]? payload = null)
        {
            var stream = _stream;
            if (stream == null || _closed) return;
            var any = Pb.New().Str(1, ConnectionProtocol.UriPrefix + op);
            if (payload != null && payload.Length > 0) any.Bytes(2, payload);
            byte[] frame = Pb.New().Msg(2, Pb.New().Bytes(1, any.Build()).Var(2, -1)).Build();

            await _writing.WaitAsync();
            try { await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame); }
            finally { _writing.Release(); }
        }

        /// <summary>What the server sends, read as long as the socket lasts: nothing it writes is left unread.</summary>
        private async Task ReadAsync(CancellationToken stop)
        {
            try
            {
                while (!stop.IsCancellationRequested && !_closed && _stream != null)
                {
                    byte[] frame = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(_stream);
                    if (frame == null) break;
                    string? op = ConnectionProtocol.ReadOpcode(frame);
                    if (op == null) continue;
                    byte[] payload = ConnectionProtocol.ReadPayload(frame, op) ?? Array.Empty<byte>();
                    try { await OnFrameAsync(op, payload); }
                    catch (Exception ex) { Program.LogDebug($"[Bots] {_name} on {op}: {ex.Message}"); }
                }
            }
            catch (Exception) { }
            finally
            {
                _closed = true;
                _list?.TrySetResult(new List<(long, string)>());
                _created?.TrySetResult(-1);
                _selected?.TrySetResult(false);
                _arrived?.TrySetResult(0);
            }
        }

        /// <summary>What it answers, and what it hands to whoever waits for it.</summary>
        private async Task OnFrameAsync(string op, byte[] payload)
        {
            switch (op)
            {
                case Op.Kvi:
                    _list?.TrySetResult(ReadCharacters(payload));
                    break;
                case Op.Kvb:
                    _created?.TrySetResult((int)Var(payload, 2));
                    break;
                case Op.Kvd:
                    // Its character is still in a fight: it goes on with it.
                    await SendAsync(Op.Kwb);
                    break;
                case Op.Kva:
                    _selected?.TrySetResult(true);
                    break;
                case Op.Kmp:
                    // The next map is a fight's: it is asked for with ijm and kmv, not with jrh.
                    _fightMapComing = Var(payload, 1) != 0;
                    break;
                case Op.Jru:
                    long map = Var(payload, 2);
                    if (_fightMapComing)
                    {
                        await SendAsync(Op.Ijm);
                        await SendAsync(Op.Kmv, Pb.New().Var(1, map).Build());
                    }
                    else
                    {
                        await SendAsync(Op.Kmv, Pb.New().Var(1, map).Build());
                        await SendAsync("jrh", Pb.New().Var(1, map).Build());
                        _arrived?.TrySetResult(map);
                    }
                    _fightMapComing = false;
                    break;
                case Op.Hqc:
                    // A duel offered to it: always yes.
                    if (Var(payload, 2) == _characterId) _ = Task.Run(() => AcceptDuelAsync((int)Var(payload, 3)));
                    break;
                case Op.Lsh:
                    // The Koliseo has found it a match: yes, after a moment.
                    _ = Task.Run(AcceptKoliseoAsync);
                    break;
                case Op.Lsx:
                    // The queue as the server has it: f1 set while it is searching.
                    if (Var(payload, 1) == 0) _queuedSince = null;
                    break;
                default:
                    await OnFightFrameAsync(op, payload);
                    break;
            }
        }

        /// <summary>A varint field of a payload, 0 when absent.</summary>
        private static long Var(byte[] payload, int field)
        {
            if (payload.Length == 0) return 0;
            try
            {
                return ProtoMessage.Parse(payload).Fields
                    .FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>The client's heartbeat, every five seconds while it is in.</summary>
        private async Task HeartbeatAsync(CancellationToken stop)
        {
            try
            {
                while (!stop.IsCancellationRequested && !_closed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stop);
                    await SendAsync(Op.Kqo, Pb.New().Var(2, 1).Build());
                }
            }
            catch (Exception) { }
        }

        /// <summary>Waits for what the reader hands over, as long as the server takes to answer.</summary>
        private static async Task<T?> WithinAsync<T>(Task<T> task, T? otherwise = default)
            => await Task.WhenAny(task, Task.Delay(Patience)) == task ? await task : otherwise;
    }
}
