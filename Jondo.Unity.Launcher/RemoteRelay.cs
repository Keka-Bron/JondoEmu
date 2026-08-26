using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// Relays the loopback endpoints expected by JondoFix to a server on another machine.
    ///
    /// Dofus still talks to 127.0.0.1: that is an invariant of the client mod and keeps the local
    /// setup unchanged. In remote mode these four listeners bridge those connections to the host
    /// stored in the launcher preferences. This is ordinary in-process TCP forwarding; it needs
    /// neither administrator rights nor persistent netsh port-proxy rules.
    /// </summary>
    internal static class RemoteRelay
    {
        private static readonly int[] Ports = { 5555, 6337, 8888, 15881 };
        private static readonly object Gate = new object();
        private static readonly List<TcpListener> Listeners = new List<TcpListener>();
        private static CancellationTokenSource? _stop;
        private static string _host = "";

        public static bool IsRunning
        {
            get { lock (Gate) return _stop != null; }
        }

        public static void Start(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("A remote server host is required.", nameof(host));

            lock (Gate)
            {
                if (_stop != null)
                {
                    if (string.Equals(_host, host.Trim(), StringComparison.OrdinalIgnoreCase)) return;
                    throw new InvalidOperationException("The remote relay is already running for another host.");
                }

                _host = host.Trim();
                _stop = new CancellationTokenSource();
                try
                {
                    foreach (int port in Ports)
                    {
                        var listener = new TcpListener(IPAddress.Loopback, port);
                        listener.Start();
                        Listeners.Add(listener);
                        _ = AcceptAsync(listener, _host, port, _stop.Token);
                    }
                }
                catch
                {
                    StopLocked();
                    throw;
                }
            }
        }

        public static void Stop()
        {
            lock (Gate) StopLocked();
        }

        private static void StopLocked()
        {
            _stop?.Cancel();
            foreach (var listener in Listeners)
            {
                try { listener.Stop(); } catch { }
            }
            Listeners.Clear();
            _stop?.Dispose();
            _stop = null;
            _host = "";
        }

        private static async Task AcceptAsync(TcpListener listener, string host, int port,
                                              CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(stop);
                    _ = ForwardAsync(client, host, port, stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"Remote relay accept on {port} failed: {ex.Message}");
                }
            }
        }

        private static async Task ForwardAsync(TcpClient local, string host, int port,
                                               CancellationToken stop)
        {
            using (local)
            using (var remote = new TcpClient())
            {
                try
                {
                    local.NoDelay = true;
                    remote.NoDelay = true;
                    await remote.ConnectAsync(host, port, stop);

                    using NetworkStream fromClient = local.GetStream();
                    using NetworkStream fromServer = remote.GetStream();
                    Task upload = fromClient.CopyToAsync(fromServer, stop);
                    Task download = fromServer.CopyToAsync(fromClient, stop);

                    Task first = await Task.WhenAny(upload, download);
                    await first;
                    try
                    {
                        if (ReferenceEquals(first, upload)) remote.Client.Shutdown(SocketShutdown.Send);
                        else local.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch { }
                    await Task.WhenAll(upload, download);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Program.LogDebug($"Remote relay {host}:{port} failed: {ex.Message}");
                }
            }
        }
    }
}
