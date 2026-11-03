using System.IO.Pipes;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Jondo.Unity.Launcher;

/// <summary>Second-instance activation without platform window handles.</summary>
internal sealed class LauncherActivation : IDisposable
{
    private static string PipeName => "jondo-launcher-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            UserDirectories.Configuration)))[..20];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;

    internal LauncherActivation()
    {
        _listener = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    var request = new byte[1];
                    if (await pipe.ReadAsync(request, _stop.Token) != 1) continue;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                            { MainWindow: { } window })
                        {
                            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                            window.Show(); window.Activate();
                        }
                    });
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (IOException error) { Console.Error.WriteLine("[Activation] " + error.Message); }
            }
        });
    }

    internal static bool Notify()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            pipe.Connect(1500);
            pipe.WriteByte(1);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
