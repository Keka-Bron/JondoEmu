using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// Restarting the server to apply new settings, with the players told first: a line in their
    /// chat, in their own language, as the countdown runs out, and then the restart.
    /// </summary>
    public static class ServerRestart
    {
        /// <summary>The moments, in seconds before the restart, at which the players are told.</summary>
        private static readonly int[] Steps = { 300, 120, 60, 30, 10, 5, 4, 3, 2, 1 };

        private static int _counting;

        /// <summary>Whether a restart is already counting down.</summary>
        public static bool Counting => _counting != 0;

        /// <summary>How many characters are in the world now.</summary>
        public static int PlayersInside => GameNodeProxy.SesionesVivas.Values.Count(s => s.HasCharacter);

        /// <summary>Starts the countdown; the server restarts when it ends. A second call changes nothing.</summary>
        public static void Begin(int seconds)
        {
            if (Interlocked.Exchange(ref _counting, 1) != 0) return;
            seconds = Math.Max(1, seconds);
            _ = Task.Run(async () =>
            {
                try
                {
                    var end = DateTime.UtcNow.AddSeconds(seconds);
                    await AnnounceAsync(seconds);
                    foreach (int step in Steps.Where(s => s < seconds))
                    {
                        var wait = end.AddSeconds(-step) - DateTime.UtcNow;
                        if (wait > TimeSpan.Zero) await Task.Delay(wait);
                        await AnnounceAsync(step);
                    }
                    var last = end - DateTime.UtcNow;
                    if (last > TimeSpan.Zero) await Task.Delay(last);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Settings] The restart countdown failed: {ex.Message}");
                }
                Program.RequestRestart("new settings");
            });
        }

        /// <summary>Tells every player in the world how long is left.</summary>
        public static async Task AnnounceAsync(int seconds)
        {
            foreach (var session in GameNodeProxy.SesionesVivas.Values.ToList())
            {
                if (!session.HasCharacter) continue;
                string text = CommandTexts.Get(session.State.Language, "server.restart", seconds);
                try { await session.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text))); }
                catch (Exception) { }
            }
            Console.WriteLine($"[Settings] The server restarts in {seconds} s to apply its new settings.");
        }
    }
}
