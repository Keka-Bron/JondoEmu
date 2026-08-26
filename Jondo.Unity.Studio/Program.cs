using System;
using Avalonia;

namespace Jondo.Unity.Studio
{
    internal static class Program
    {
        /// <summary>
        /// The editor's entry point.
        /// </summary>
        /// <remarks>
        /// It needs no server: the editor opens content/ and the databases and works on its own.
        /// Talking to a live server is a separate, thin channel that only says "reload this
        /// domain", and it comes later.
        ///
        /// One argument, <c>--selftest</c>, builds every section against the real data and exits
        /// without showing a window. See <see cref="SelfTest"/> for why that is worth a flag.
        /// </remarks>
        [STAThread]
        public static int Main(string[] args)
        {
            bool selfTest = Array.Exists(args,
                argument => string.Equals(argument, "--selftest", StringComparison.OrdinalIgnoreCase));

            if (!selfTest) return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            // Avalonia's services are set up but no lifetime is started, because a control cannot
            // be constructed before the first and there is no window to run in the second. Going
            // through the desktop lifetime and shutting it down without ever giving it a window
            // exits through the back door with 0xE0434352, which reads as "the self test crashed"
            // even when every section built.
            BuildAvaloniaApp().SetupWithoutStarting();
            return SelfTest.Run();
        }

        /// <summary>Used by the entry point and by Avalonia's design-time tooling.</summary>
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<StudioApp>()
                         .UsePlatformDetect()
                         .LogToTrace();
    }
}
