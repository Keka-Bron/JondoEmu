using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Launcher.Handlers;

namespace Jondo.Unity.Launcher
{
    class Program
    {
        public static readonly int haapiPort = 8888;
        public static readonly int port = 15881;
        public static readonly int gamePort = 5555;
        public static readonly int gameNodePort = 5556;

        private static readonly object LogLock = new object();

        static async Task Main(string[] args)
        {
            ConsoleLogBuffer.Initialize();
            try { Console.Clear(); } catch { }
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("======================================================================");
            Console.WriteLine("                JONDO EMULATOR LAUNCHER (MODULAR C#)                  ");
            Console.WriteLine("======================================================================");
            Console.ResetColor();

            // 0. Resolved data paths. Everything the emulator needs now lives inside its own
            //    folder; the root is derived from the assembly location, not hardcoded.
            Paths.LogResolvedPaths();

            // 1. Initialize Database and Map Manager
            Console.WriteLine("[+] Initializing Database...");
            DatabaseManager.Initialize();

            Console.WriteLine("[+] Initializing MobSpawnManager...");
            Managers.MobSpawnManager.InitializeAndSpawnAll();

            Console.WriteLine("[+] Initializing Map Manager...");
            MapManager.Initialize();
            ExperienceTable.Initialize();
            Managers.SpellTable.Initialize();
            Managers.BreedStatCost.Initialize();
            Managers.DungeonManager.Initialize();
            Managers.EffectTable.Initialize();
            Managers.ItemSets.Initialize();
            Managers.EffectFields.Initialize();
            Managers.Interactives.Initialize();
            Managers.HavenBagStore.Initialize();
            Managers.Wardrobe.Initialize();
            Managers.Titles.Initialize();
            Managers.Cosmetics.Initialize();
            Managers.Merkasako.Initialize();
            Managers.Mounts.Initialize();
            Managers.Npcs.Initialize();
            Managers.NpcShops.Initialize();

            Console.WriteLine("[+] Registering Fight Packet Handlers...");
            Handlers.FightHandler.RegisterHandlers();

            Console.WriteLine("[+] Loading the world entry blocks...");
            WorldEntry.Initialize();

            // After the blocks and not before: part of what the check compares against the capture
            // is read out of them, the characteristic containers among it.
            RegressionGuardTests.Run();



            // 3. Start Emulation Servers
            Console.WriteLine("[+] Starting services...");
            try
            {
                HaapiServer.Start(haapiPort);
                ZaapServer.Start(port);
                GameServerProxy.Start(gamePort);
                GameNodeProxy.Start(gameNodePort);
                ChatServer.Start(6337);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[!] Critical Error starting servers: {ex.Message}");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n[+] ALL EMULATION SERVICES ONLINE AND READY!");
            Console.WriteLine("Type /help for a list of developer commands.\n");
            Console.ResetColor();

            // 4. Native launcher window (WinForms). The web interface used to be opened in a
            //    browser with --app; now it is a desktop window that calls LauncherService
            //    directly, without going through HTTP.
            AppDomain.CurrentDomain.ProcessExit += (s, e) => StopServices();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                RequestShutdown("Ctrl+C");
            };

            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[+] Opening the launcher window...");
                Console.ResetColor();

                UI.LauncherWindow.OpenOnDedicatedThread();
            }
            catch (Exception ex)
            {
                // Without the window there is no way to close the emulator, and the process would
                // stay alive in the background with nothing visible. Better to shut it down.
                Console.WriteLine($"[-] Could not open the launcher window: {ex.Message}");
                RequestShutdown("the launcher window could not be opened");
            }

            // 5. The process lives as long as the window does. Closing it stops everything.
            Console.WriteLine("[+] All servers initialized and operational. Launcher UI active.");

            await _shutdown.Task;

            StopServices();

            // Safety net: if something gets stuck and the process does not end on its own, force
            // the exit. It used to have to be killed by hand from the task manager.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                Console.WriteLine("[!] The graceful shutdown did not finish in time. Forcing the exit.");
                Environment.Exit(0);
            });
        }

        private static readonly TaskCompletionSource _shutdown =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private static int _shutdownRequested;
        private static int _servicesStopped;

        /// <summary>
        /// Requests a graceful shutdown of the emulator. The launcher window calls it when closing.
        /// It is idempotent: it does not matter how many times it is called.
        /// </summary>
        public static void RequestShutdown(string reason)
        {
            if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;
            Console.WriteLine($"[+] Shutting down the emulator ({reason})...");
            _shutdown.TrySetResult();
        }

        private static void StopServices()
        {
            if (Interlocked.Exchange(ref _servicesStopped, 1) != 0) return;
            Console.WriteLine("[+] Stopping services...");
            try { HaapiServer.Stop(); } catch { }
            try { ZaapServer.Stop(); } catch { }
            try { GameServerProxy.Stop(); } catch { }
            try { GameNodeProxy.Stop(); } catch { }
            try { ChatServer.Stop(); } catch { }
            Console.WriteLine("[+] Services stopped.");
        }

        public static void LogDebug(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);
            lock (LogLock)
            {
                try
                {
                    File.AppendAllText(Paths.DebugLog, line + "\r\n");
                }
                catch { }
            }
        }
    }
}
