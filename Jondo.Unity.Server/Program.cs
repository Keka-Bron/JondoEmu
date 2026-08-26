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

            if (!Contract.CogerElSitio("JondoEmuServidor"))
            {
                Console.WriteLine("[!] Ya hay un servidor de Jondo corriendo en esta sesión. Este se cierra.");
                await Task.Delay(2500);
                return;
            }

            try { await ArrancarTodoYEsperar(); }
            finally { Contract.SoltarElSitio(); }
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
            Managers.JobManager.Initialize();
            Managers.SkillManager.Initialize();
            Managers.RecipeManager.Initialize();
            Managers.Interactives.Initialize();
            Managers.HavenBagStore.Initialize();
            Managers.Wardrobe.Initialize();
            Managers.Titles.Initialize();
            Managers.Cosmetics.Initialize();
            Managers.EquipmentSkins.Initialize();
            Managers.Merkasako.Initialize();
            Managers.Zaapis.Initialize();
            Managers.Bins.Initialize();
            Managers.Anomalies.Initialize();
            Managers.Houses.Initialize();
            // Detrás de Houses a propósito: TeleportManager rechaza las rutas que caen sobre una
            // puerta de casa, y para eso las casas tienen que estar ya cargadas.
            Managers.TeleportManager.Initialize();
            Managers.Resources.Initialize();
            Managers.InfoMessages.Initialize();
            Managers.Challenges.Initialize();
            Managers.Challenges.OnlyOffer(Handlers.ChallengeWatcher.Watched);
            Managers.InteractiveRegistry.Initialize();
            Managers.Mounts.Initialize();
            // Vendors va PRIMERO: Npcs necesita saber ya a quien no debe sembrar, y NpcShops a
            // quien le echa encima el catalogo de quien.
            Network.UnknownPackets.Initialize();
            Managers.Vendors.Initialize();
            Managers.Npcs.Initialize();
            Managers.NpcShops.Initialize();
            Managers.TokenShops.Initialize();

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
                Console.WriteLine($"[+] Network binding: {ServerBinding.Description}.");
                if (ServerBinding.Public)
                {
                    Console.WriteLine("[!] Public binding does not encrypt traffic. Use it only on " +
                                      "a trusted network or behind a VPN/tunnel.");
                }
                // La llave con la que el lanzador podrá hablarle a este servidor. Una por arranque:
                // así un lanzador de una sesión anterior no se queda con llave de la de ahora.
                ControlApi.NuevoSecreto();
                Console.WriteLine($"[+] Llave del canal de mando en {Contract.FicheroDelSecreto}");
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
            Console.ResetColor();

            // Aquí también se invitaba a escribir /help. No hay dónde: esto es un WinExe sin
            // consola y nadie lee la entrada estándar —no queda un solo Console.ReadLine en el
            // servidor—. Los comandos de verdad son los del chat del juego, y los reparte
            // CommandHandler según el rol de quien los escribe.

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

            // Y su ventana: el registro y las cifras. Si no se pudiera abrir —sin escritorio, por
            // ejemplo— el servidor sigue funcionando igual: la ventana es para mirar, no para que
            // las cosas pasen.
            UI.ServerWindow.Abrir();

            // Aquí se anunciaba «el servidor está en marcha, Ctrl+C para pararlo» y «cerrar el
            // lanzador ya no apaga esto». Eran avisos para quien miraba una consola de texto:
            // ahora hay una ventana con un botón de parar, y lo que el registro tiene que contar
            // es lo que pasa en el servidor, no cómo se maneja.

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

        /// <summary>Si ya se ha pedido el apagado, para que la ventana no vuelva a preguntar.</summary>
        public static bool ApagandoYa => _shutdownRequested != 0;

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

        /// <summary>
        /// Una línea en el registro de depuración.
        ///
        /// Escribía con File.AppendAllText, que abre el fichero, escribe y lo cierra, en CADA
        /// línea; y la ruta se resolvía cada vez, con su Directory.Exists dentro. Esto se llama
        /// constantemente durante un combate. Ahora el manejador se queda abierto y la ruta se
        /// resuelve una sola vez, pero se sigue vaciando línea a línea: un registro de depuración
        /// tiene que tener escrito lo último que pasó justo cuando el servidor se muere.
        /// </summary>
        public static void LogDebug(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);
            LogFile.Debug.WriteLine(line);
        }
    }
}
