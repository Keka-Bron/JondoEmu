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
        /// It takes no arguments and needs no server: the editor opens content/ and the databases
        /// and works on its own. Talking to a live server is a separate, thin channel that only
        /// says "reload this domain", and it comes later.
        /// </remarks>
        [STAThread]
        public static int Main(string[] args)
            => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        /// <summary>Used by the entry point and by Avalonia's design-time tooling.</summary>
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<StudioApp>()
                         .UsePlatformDetect()
                         .LogToTrace();
    }
}
