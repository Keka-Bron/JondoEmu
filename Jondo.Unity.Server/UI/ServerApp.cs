using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Jondo.Unity.Server.UI;

internal sealed class ServerApp : Application
{
    internal static Task BackendTask { get; private set; }
    internal static void Run(string[] args)
        => AppBuilder.Configure<ServerApp>().UsePlatformDetect().LogToTrace()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new ServerWindow();
            // Main runs on the original STA thread, including the lifetime of the instance mutex.
            // Service initialization is synchronous in places and must not block desktop dispatch.
            BackendTask = Task.Run(Program.RunServerAsync);
            _ = ObserveBackend(desktop, BackendTask);
            desktop.ShutdownRequested += (_, e) =>
            {
                e.Cancel = true;
                Program.RequestShutdown("desktop session ending");
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ObserveBackend(IClassicDesktopStyleApplicationLifetime desktop, Task backend)
    {
        await WaitForExitAsync(backend, () => Program.Failure != null, Program.ShutdownRequested).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => desktop.Shutdown(Environment.ExitCode));
    }

    internal static async Task WaitForExitAsync(Task backend, Func<bool> failed, Task shutdown)
    {
        // Shutdown never tears the UI down before service cleanup finishes. Fatal startup stays
        // visible until the operator closes it (or the OS asks the process to stop).
        await backend.ConfigureAwait(false);
        if (failed()) await shutdown.ConfigureAwait(false);
    }
}
