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

        /// <summary>
        /// El servidor. Sin ventanas: desde que el lanzador es otro ejecutable, aquí dentro no
        /// queda ni una línea de WinForms.
        ///
        /// Antes esto arrancaba los cinco servicios y después abría la ventana del lanzador, y la
        /// vida del proceso quedaba enchufada al ciclo de vida de un Form: cerrar la ventana
        /// llamaba a RequestShutdown y se apagaba todo, con los jugadores que hubiera dentro.
        /// </summary>
        static async Task Main(string[] args)
        {
            ConsoleLogBuffer.Initialize();

            if (!Contrato.CogerElSitio("JondoEmuServidor"))
            {
                Console.WriteLine("[!] Ya hay un servidor de Jondo corriendo en esta sesión. Este se cierra.");
                await Task.Delay(2500);
                return;
            }

            try { await ArrancarTodoYEsperar(); }
            finally { Contrato.SoltarElSitio(); }
        }

        private static async Task ArrancarTodoYEsperar()
        {
            try { Console.Clear(); } catch { }
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("======================================================================");
            Console.WriteLine("                        JONDO — SERVIDOR                             ");
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
                // La llave con la que el lanzador podrá hablarle a este servidor. Una por arranque:
                // así un lanzador de una sesión anterior no se queda con llave de la de ahora.
                ApiDeControl.NuevoSecreto();
                Console.WriteLine($"[+] Llave del canal de mando en {Contrato.FicheroDelSecreto}");
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

            AppDomain.CurrentDomain.ProcessExit += (s, e) => StopServices();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                RequestShutdown("Ctrl+C");
            };

            // Los lanzamientos que se quedan colgados.
            //
            // Un cliente que arranca y nunca llega a conectar al 5555 —o un lanzador que se cierra
            // justo en medio— dejaba la cuenta marcada como ocupada PARA SIEMPRE, y el registro la
            // rechazaba en cada intento posterior. El CreatedAtUtc llevaba puesto desde el principio
            // sin que lo leyera nadie; ahora es lo que las suelta.
            var barrendero = new System.Threading.Timer(
                _ => { try { Network.ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.FromMinutes(5)); } catch { } },
                null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            Console.WriteLine("[+] El servidor está en marcha. Ctrl+C para pararlo, o el botón del lanzador.");
            Console.WriteLine("[+] Cerrar el lanzador YA NO apaga esto.\n");

            await _shutdown.Task;
            await barrendero.DisposeAsync();

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
