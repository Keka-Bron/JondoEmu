using System;
using System.Net;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// Chooses whether emulator services are reachable only from this machine or from a network.
    /// Local remains the safe default. A cloud deployment opts in with JONDO_PUBLIC_BIND=1 and
    /// must put the HTTP control/HAAPI endpoint behind an HTTPS reverse proxy before exposing it.
    /// </summary>
    internal static class ServerBinding
    {
        public static bool Public
        {
            get
            {
                string value = (Environment.GetEnvironmentVariable("JONDO_PUBLIC_BIND") ?? "").Trim();
                return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static IPAddress TcpAddress => Public ? IPAddress.Any : IPAddress.Loopback;

        /// <summary>
        /// Trusted-LAN escape hatch. Internet deployments should leave this off and terminate
        /// launcher control at a reverse proxy running on the server itself.
        /// </summary>
        public static bool AllowInsecureRemoteControl
        {
            get
            {
                string value = (Environment.GetEnvironmentVariable("JONDO_ALLOW_INSECURE_CONTROL") ?? "").Trim();
                return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void ConfigureHttp(HttpListener listener, int port)
        {
            if (Public)
            {
                // HttpListener wildcard bindings can require a URL ACL when the server is not
                // running elevated. The startup exception deliberately remains visible so a
                // cloud operator cannot believe the endpoint is public when it is not.
                listener.Prefixes.Add($"http://+:{port}/");
            }
            else
            {
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            }
        }

        public static string Description => Public ? "all network interfaces" : "loopback only";

        /// <summary>
        /// Raw port 8888 remains reachable for Dofus HAAPI in public mode, but its launcher
        /// control routes contain passwords and tokens. Only a local TLS reverse proxy may reach
        /// those routes unless the operator explicitly opts into trusted-LAN plaintext control.
        /// </summary>
        public static bool MayUseControlApi(HttpListenerRequest request)
            => MayUseControlApi(request.RemoteEndPoint.Address, Public,
                                AllowInsecureRemoteControl);

        internal static bool MayUseControlApi(IPAddress peer, bool publicMode,
                                              bool allowInsecureRemoteControl)
            => !publicMode || IPAddress.IsLoopback(peer) || allowInsecureRemoteControl;

        /// <summary>
        /// Returns the player address. X-Forwarded-For is trusted only from a loopback proxy, so a
        /// remote client cannot spoof the per-IP account/launch limits by adding its own header.
        /// </summary>
        public static string ControlClientAddress(HttpListenerRequest request)
            => ControlClientAddress(request.RemoteEndPoint.Address,
                                    request.Headers["X-Forwarded-For"]);

        internal static string ControlClientAddress(IPAddress peer, string? forwardedFor)
        {
            if (IPAddress.IsLoopback(peer))
            {
                string first = (forwardedFor ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault() ?? "";
                if (IPAddress.TryParse(first, out IPAddress? forwarded)) return forwarded.ToString();
            }
            return peer.ToString();
        }
    }
}
