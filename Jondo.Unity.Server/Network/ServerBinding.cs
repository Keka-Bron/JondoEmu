using System;
using System.Net;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// What the emulator's ports are bound to: only this machine, or the whole network.
    ///
    /// Of the five services the server opens, the game proxy, the zaap and the HAAPI were
    /// bound to <c>127.0.0.1</c>, while the chat and the game node used
    /// <c>IPAddress.Any</c>. It was not a deployment decision: they were listeners written at different
    /// times. All five remain closed by default and open together when remote
    /// mode is asked for.
    ///
    /// So now the four go the same, and to open them one has to ask for it on purpose with
    /// <c>JONDO_PUBLIC_BIND=1</c>. It is needed when the server lives on another machine; until
    /// then, closed.
    ///
    /// The idea comes from Raphaël's pull request, which brought an equivalent file. The code is
    /// ours: here only the two lines really used are needed.
    /// </summary>
    public static class ServerBinding
    {
        /// <summary>Have the ports been asked to be open to the network?</summary>
        public static bool Public
        {
            get
            {
                string valor = (Environment.GetEnvironmentVariable("JONDO_PUBLIC_BIND") ?? "").Trim();
                return valor == "1"
                    || valor.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || valor.Equals("si", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>The address to bind to. Closed unless asked otherwise.</summary>
        public static IPAddress TcpAddress => Public ? IPAddress.Any : IPAddress.Loopback;

        /// <summary>For the server log, so it says which door it started with.</summary>
        public static string Description => Public ? "toda la red" : "sólo esta máquina";
    }
}
