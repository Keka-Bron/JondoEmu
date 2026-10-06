using Jondo.Unity.Launcher;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server
{
    class Program
    {
        public static readonly int haapiPort = 8888;
        public static readonly int port = 15881;
        public static readonly int gamePort = 5555;
        public static readonly int gameNodePort = 5556;

        private static readonly object LogLock = new object();

        /// <summary>
        /// The server. No windows: since the launcher is another executable, not a single
        /// line of WinForms is left in here.
        ///
        /// Before, this started the five services and then opened the launcher window, and the
        /// process's life was plugged into a Form's life cycle: closing the window
        /// called RequestShutdown and everything shut down, with whatever players were inside.
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

            try
            {
                await ArrancarTodoYEsperar();
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                string failure = StartupFailure(ex);
                Console.WriteLine(failure);
                LogFile.Debug.WriteLine(failure);
            }
            finally
            {
                Contract.SoltarElSitio();
            }
        }

        /// <summary>The complete failure written before a WinExe exits during startup.</summary>
        internal static string StartupFailure(Exception error)
            => $"[!] Fatal error while starting Jondo Server:{Environment.NewLine}{error}";

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
            // In front of the monsters on purpose, and this was a real bug: the
            // seeder reads DungeonRooms so as not to empty the dungeons with the indoor
            // ban, and whoever writes that table is DungeonManager. Being behind, what
            // it read was what the PREVIOUS start had left written. Now it also puts the
            // boss in his last room, which without this does not exist.
            Managers.DungeonManager.Initialize();
            Managers.MobSpawnManager.InitializeAndSpawnAll();

            Console.WriteLine("[+] Initializing Map Manager...");
            MapManager.Initialize();
            ExperienceTable.Initialize();
            Managers.SpellTable.Initialize();
            Managers.BreedStatCost.Initialize();
            Managers.EffectTable.Initialize();
            Managers.ItemSets.Initialize();
            Managers.EffectFields.Initialize();
            Managers.JobManager.Initialize();
            Managers.SkillManager.Initialize();
            Managers.RecipeManager.Initialize();
            Managers.Interactives.Initialize();
            Managers.HavenBagStore.Initialize();
            Managers.StorageStacks.Initialize();
            Managers.Bank.Initialize();
            Managers.Wardrobe.Initialize();
            Managers.Titles.Initialize();
            Managers.Cosmetics.Initialize();
            Managers.EquipmentSkins.Initialize();
            Managers.KoliseoMaps.Initialize();
            // The jail's sentences still running, and the clock that ends them.
            Managers.Jail.Initialize();
            Managers.Dreams.Initialize();
            Managers.Merkasako.Initialize();
            Managers.Zaapis.Initialize();
            Managers.Bins.Initialize();
            Managers.Anomalies.Initialize();
            Managers.Houses.Initialize();
            Managers.HouseStore.Initialize();
            Managers.GuildChests.Initialize();
            // After Houses on purpose: TeleportManager rejects the routes that fall on a
            // house door, and for that the houses have to be loaded already.
            Managers.TeleportManager.Initialize();
            Managers.Resources.Initialize();
            Managers.Workshops.Initialize();
            // Before the registry, which declares their counters; the book of lots on sale with
            // them, and what ran out of time while the server was down goes back to its sellers.
            Managers.Marketplaces.Initialize();
            Managers.MarketplaceListings.Initialize();
            Handlers.MarketplaceHandler.SweepExpiredAsync().GetAwaiter().GetResult();
            Managers.Forgemagic.Initialize();
            Managers.InfoMessages.Initialize();
            Managers.Challenges.Initialize();
            Managers.Challenges.OnlyOffer(Handlers.ChallengeWatcher.Watched);
            Managers.InteractiveRegistry.Initialize();
            Managers.Mounts.Initialize();
            // Vendors goes FIRST: Npcs already needs to know whom not to seed, and NpcShops
            // on whom to pile whose catalogue.
            Network.UnknownPackets.Initialize();
            Managers.Vendors.Initialize();
            Managers.Npcs.Initialize();

            // After the NPCs on purpose: it reads their templates to know with which reply
            // each guardian offers the bundle and with which the key.
            Managers.DungeonDoor.Initialize();
            // Behind the NPCs too, and for the same reason: the bankers are found in their templates.
            Managers.Bankers.Initialize();
            Managers.NpcShops.Initialize();
            // After Npcs because the quests hang from their dialogues, and the catalogue is
            // Ankama's and does not change: it is read once and all the sessions share it.
            Managers.Quests.Load();
            Managers.Readables.Load();
            // After the quests: 259 achievements are earned by finishing one, and the catalogue is indexed
            // by quest when loaded.
            Managers.Achievements.Load();
            // Behind the quests as well: the Almanax offerings are quests, and the calendar finds
            // their giver in the quest catalogue.
            Managers.Almanax.Load();
            Managers.Emotes.Load();
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
                // The key the launcher will be able to talk to this server with. One per start:
                // that way a launcher from a previous session does not keep a key to the current one.
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

            // Here too one was invited to type /help. There is nowhere to: this is a WinExe with no
            // console and nobody reads standard input —not a single Console.ReadLine is left in the
            // server—. The real commands are those of the game chat, and
            // CommandHandler hands them out according to the role of whoever types them.

            AppDomain.CurrentDomain.ProcessExit += (s, e) => StopServices();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                RequestShutdown("Ctrl+C");
            };

            // The launches that are left hanging.
            //
            // A client that starts and never gets to connect to 5555 —or a launcher that closes
            // right in the middle— left the account marked as busy FOREVER, and the registry
            // rejected it on every later attempt. CreatedAtUtc had been set from the start
            // without anyone reading it; now it is what releases them.
            var barrendero = new System.Threading.Timer(
                _ => { try { Network.ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.FromMinutes(5)); } catch { } },
                null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            // The Koliseo's queues, looked at again every few seconds: the rating window widens
            // with the wait, so a match that was not possible at enrolment may be now.
            var koliseo = new System.Threading.Timer(
                _ =>
                {
                    try { Handlers.KoliseoHandler.TickAsync().GetAwaiter().GetResult(); }
                    catch (Exception ex) { Console.WriteLine($"[Koliseo] The queue tick failed: {ex.Message}"); }
                },
                null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            // And its window: the log and the figures. If it could not be opened —no desktop, for
            // example— the server keeps working the same: the window is for looking, not for
            // things to happen.
            UI.ServerWindow.Abrir();

            // Here «el servidor está en marcha, Ctrl+C para pararlo» and «cerrar el
            // lanzador ya no apaga esto» used to be announced. They were notices for whoever watched a text console:
            // now there is a window with a stop button, and what the log has to tell
            // is what happens on the server, not how it is operated.

            await _shutdown.Task;
            await barrendero.DisposeAsync();
            await koliseo.DisposeAsync();

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

        /// <summary>Whether shutdown has already been asked for, so the window does not ask again.</summary>
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
        /// A line in the debug log.
        ///
        /// It wrote with File.AppendAllText, which opens the file, writes and closes it, on EVERY
        /// line; and the path was resolved every time, with its Directory.Exists inside. This is called
        /// constantly during a fight. Now the handle stays open and the path is
        /// resolved only once, but it is still flushed line by line: a debug log
        /// has to have written the last thing that happened right when the server dies.
        /// </summary>
        public static void LogDebug(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);
            LogFile.Debug.WriteLine(line);
        }
    }
}
