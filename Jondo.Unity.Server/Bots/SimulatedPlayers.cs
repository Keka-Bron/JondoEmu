using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jondo.Unity.Server.Network;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// The simulated players (<see cref="ServerSettings.SimulatedPlayers"/>): JondoBot-001,
    /// JondoBot-002... each an account of its own that comes in the way the client does -- a token,
    /// the connection server, a ticket, the character list, the world -- over a socket of its own,
    /// and then lives there (<see cref="SimulatedPlayer"/>).
    /// </summary>
    /// <remarks>
    /// Asked for on 2026-11-01: a variable number of bots that come and go, of random classes,
    /// named JondoBot-XXX after their number, that walk, fight monsters now and then, gather, enrol
    /// in the Koliseo and fight there, and always accept a duel -- for a world that feels inhabited
    /// and to put the server under the load of that many players, "each with its own connection,
    /// as if it were a real player".
    ///
    /// So everything a bot does goes through its socket as the client's own messages, and the
    /// server cannot tell it from a player: the handlers, the broadcasts, the fights and the saving
    /// all run for it. What it reads to decide -- the monsters on its map, its fight's board -- it
    /// reads from the server's own state, as a client reads its screen.
    ///
    /// How many are in drifts between half of <see cref="ServerSettings.SimulatedPlayersMax"/> and
    /// all of it: one comes in or goes every few seconds, and each stays a while and then leaves,
    /// as players do. Their accounts and characters stay from one start to the next.
    /// </remarks>
    public static class SimulatedPlayers
    {
        /// <summary>The most the setting allows: room for them and for the people in the 500 sessions.</summary>
        public const int Most = 200;

        /// <summary>A bot's character name: JondoBot- and its number.</summary>
        public const string NamePrefix = "JondoBot-";

        private const string LoginPrefix = "jondobot-";

        private static readonly ConcurrentDictionary<int, SimulatedPlayer> _online = new();
        private static readonly ConcurrentDictionary<long, int> _accounts = new();
        private static readonly ConcurrentDictionary<int, byte> _ports = new();
        private static CancellationTokenSource? _stop;

        /// <summary>How often the window hears how many are in, at most.</summary>
        private static readonly TimeSpan Summary = TimeSpan.FromMinutes(5);

        /// <summary>How many are connected now.</summary>
        public static int Online => _online.Count;

        /// <summary>Whether an account is one of the bots'.</summary>
        public static bool IsSimulated(long accountId) => _accounts.ContainsKey(accountId);

        internal static string NameOf(int number) => $"{NamePrefix}{number:D3}";

        /// <summary>
        /// A socket to the server for a bot, its local port known before it connects: the server
        /// recognises the connection from its first line, and logs nothing of it
        /// (<see cref="IsBotSocket"/>). The port is let go with <see cref="Hangup"/>.
        /// </summary>
        internal static async Task<TcpClient> DialAsync(CancellationToken stop)
        {
            var tcp = new TcpClient(new IPEndPoint(IPAddress.Loopback, 0)) { NoDelay = true };
            int port = ((IPEndPoint)tcp.Client.LocalEndPoint!).Port;
            _ports[port] = 0;
            try
            {
                await tcp.ConnectAsync(IPAddress.Loopback, Program.gamePort, stop);
                return tcp;
            }
            catch
            {
                Hangup(tcp, port);
                throw;
            }
        }

        /// <summary>Closes a bot's socket and forgets its port, which the system may give anybody next.</summary>
        internal static void Hangup(TcpClient? tcp, int port = 0)
        {
            try
            {
                if (port == 0 && tcp?.Client?.LocalEndPoint is IPEndPoint local) port = local.Port;
            }
            catch (ObjectDisposedException) { }
            try { tcp?.Close(); } catch { }
            if (port != 0) _ports.TryRemove(port, out _);
        }

        /// <summary>Whether these characters are all bots: what happens among them alone is not logged.</summary>
        internal static bool OnlyBots(IEnumerable<long> characters)
        {
            bool any = false;
            foreach (long character in characters)
            {
                any = true;
                if (SessionRegistry.FindByCharacter(character) is not { Quiet: true }) return false;
            }
            return any;
        }

        /// <summary>Whether the other end of a server socket is one of the bots'.</summary>
        internal static bool IsBotSocket(EndPoint? remote)
            => remote is IPEndPoint ip && IPAddress.IsLoopback(ip.Address) && _ports.ContainsKey(ip.Port);

        /// <summary>Starts them, if the settings say so. Called once the services are listening.</summary>
        public static void Start(ServerSettings settings)
        {
            if (!settings.SimulatedPlayers || _stop != null) return;
            int most = Math.Clamp(settings.SimulatedPlayersMax, 1, Most);
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            Console.WriteLine($"[Bots] Simulated players on: up to {most} at once.");
            _ = Task.Run(() => LiveAsync(most, token));
        }

        /// <summary>Takes them all out of the world, on the way down.</summary>
        public static void Stop()
        {
            var stop = Interlocked.Exchange(ref _stop, null);
            if (stop == null) return;
            stop.Cancel();
            foreach (var bot in _online.Values) bot.Disconnect();
            _online.Clear();
        }

        /// <summary>
        /// The population: how many it wants drifts a step at a time between half the most and the
        /// most, and one bot comes in or leaves every few seconds until it is there.
        /// </summary>
        private static async Task LiveAsync(int most, CancellationToken token)
        {
            var dice = new Random();
            int fewest = Math.Max(1, (most + 1) / 2);
            int wanted = dice.Next(fewest, most + 1);
            int told = -1;
            var toldAt = DateTime.MinValue;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var (number, gone) in _online.Where(b => b.Value.Finished).ToList())
                        _online.TryRemove(number, out _);

                    // Their own lines say nothing (ConsoleLogBuffer.QuietFromHere): a count, now and then.
                    if (_online.Count != told && DateTime.UtcNow - toldAt >= Summary)
                    {
                        told = _online.Count;
                        toldAt = DateTime.UtcNow;
                        Console.WriteLine($"[Bots] {told} simulated players in the world.");
                    }

                    if (dice.Next(8) == 0) wanted = Math.Clamp(wanted + dice.Next(-2, 3), fewest, most);

                    if (_online.Count < wanted)
                    {
                        var free = Enumerable.Range(1, most).Where(n => !_online.ContainsKey(n)).ToList();
                        if (free.Count > 0) Connect(free[dice.Next(free.Count)], token);
                    }
                    else if (_online.Count > wanted)
                    {
                        var staying = _online.Values.Where(b => !b.Leaving).ToList();
                        if (staying.Count > 0) staying[dice.Next(staying.Count)].LeaveSoon();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Bots] The population tick failed: {ex.Message}");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(dice.Next(2, 6)), token); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Bot number <paramref name="number"/> comes in: its account, then its own connection.</summary>
        private static void Connect(int number, CancellationToken token)
        {
            long account = AccountOf(number);
            if (account <= 0) return;
            var bot = new SimulatedPlayer(number, account);
            if (!_online.TryAdd(number, bot)) return;
            _ = Task.Run(() => bot.RunAsync(token));
        }

        /// <summary>
        /// The bot's account, made the first time: jondobot-NNN, a password nobody knows, and the
        /// bot's name as its nickname.
        /// </summary>
        internal static long AccountOf(int number)
        {
            string login = LoginPrefix + number.ToString("D3");
            long id = DatabaseManager.AccountIdOf(login);
            if (id <= 0)
            {
                string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
                if (!DatabaseManager.RegisterNewAccount(login, password, NameOf(number), "127.0.0.1", out string error))
                {
                    Console.WriteLine($"[Bots] The account of {NameOf(number)} could not be made: {error}");
                    return 0;
                }
                id = DatabaseManager.AccountIdOf(login);
            }
            if (id > 0) _accounts[id] = number;
            return id;
        }
    }
}
