using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class NetworkLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("invalid-tls")]
    [InlineData("disconnect")]
    [InlineData("disposed")]
    public async Task Chat_handshake_failures_finish_without_faulting_after_socket_disposal(string scenario)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient(AddressFamily.InterNetwork);
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port).WaitAsync(Timeout);
        using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(Timeout);
        string endpoint = accepted.Client.RemoteEndPoint!.ToString()!;
        var handle = accepted.Client.SafeHandle;
        var messages = new List<string>();

        if (scenario == "disposed") accepted.Dispose();
        Task handling = ChatServer.HandleClientAsync(accepted, certificate, messages.Add);
        try
        {
            if (scenario == "invalid-tls")
                await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n")).AsTask().WaitAsync(Timeout);
            peer.Dispose();
            // Await the real handler: a secondary logging exception must fail this assertion,
            // rather than escape through an unobserved fire-and-forget task later.
            await handling.WaitAsync(Timeout);
            Assert.True(handle.IsClosed);
            string message = Assert.Single(messages);
            Assert.StartsWith("[Chat Server] Connection from ", message);
            if (scenario != "disposed") Assert.Contains(endpoint, message);
            Assert.Contains(" ended: ", message);
        }
        finally
        {
            peer.Dispose();
            accepted.Dispose();
            if (!handling.IsCompleted) await handling.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Prefixed_reads_preserve_cancellation_instead_of_wrapping_it_as_a_fault()
    {
        using var body = new MemoryStream(new byte[] { 30 });
        using var stream = new PrefixedStream(new byte[] { 10, 20 }, 2, body);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<int> read = stream.ReadAsync(new byte[3], 0, 3, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(read.IsCanceled);
    }

    [Fact]
    public async Task Prefixed_reads_keep_prefix_and_body_bytes_in_order()
    {
        using var body = new MemoryStream(new byte[] { 30, 40, 50 });
        using var stream = new PrefixedStream(new byte[] { 10, 20 }, 2, body);
        var result = new byte[5];
        Assert.Equal(1, await stream.ReadAsync(result, 0, 1));
        Assert.Equal(3, await stream.ReadAsync(result, 1, 3));
        Assert.Equal(1, await stream.ReadAsync(result, 4, 1));
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, result);
    }

    [Fact]
    public void Only_requested_shutdown_cancellation_is_suppressed()
    {
        using var shutdown = new CancellationTokenSource();
        var direct = new OperationCanceledException(shutdown.Token);
        var wrapped = new AggregateException(new AggregateException(direct), new TaskCanceledException());
        Assert.False(ZaapServer.IsShutdownCancellation(direct, shutdown.Token));
        Assert.False(ZaapServer.IsShutdownCancellation(wrapped, shutdown.Token));

        shutdown.Cancel();
        Assert.True(ZaapServer.IsShutdownCancellation(direct, shutdown.Token));
        Assert.True(ZaapServer.IsShutdownCancellation(wrapped, shutdown.Token));
        Assert.False(ZaapServer.IsShutdownCancellation(new IOException("real transport failure"), shutdown.Token));
        Assert.False(ZaapServer.IsShutdownCancellation(new AggregateException(direct, new IOException("real failure")), shutdown.Token));
        Assert.False(ZaapServer.IsShutdownCancellation(new AggregateException(), shutdown.Token));
        Assert.False(ZaapServer.IsShutdownCancellation(direct, CancellationToken.None));
    }
}
