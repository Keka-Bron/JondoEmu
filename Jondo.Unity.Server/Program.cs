using Jondo.Unity.Launcher;
using System;
using System.IO;
using System.Runtime.InteropServices;
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


        /// <summary>The desktop owns the main thread; the same server can run without a desktop.</summary>
        [STAThread]
        static void Main(string[] args)
        {
            ConsoleLogBuffer.Initialize();

            // Started again by the one before it, to apply its settings: it waits for that one to be gone.
            WaitForThePreviousRun(args);

            if (!Contract.CogerElSitio("JondoEmuServidor"))
            {
                Console.WriteLine("[!] There is already a Jondo server running in this session. This one closes.");
                Thread.Sleep(2500);
                return;
            }

            try
            {
                _headless = UI.ServerLaunch.Headless(args);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => StopServices();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    RequestShutdown("Ctrl+C");
                };
                using var sigterm = OperatingSystem.IsWindows() ? null :
                    PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                    {
                        context.Cancel = true;
                        RequestShutdown("SIGTERM");
                    });
                if (_headless) RunServerAsync().GetAwaiter().GetResult();
                else UI.ServerApp.Run(args);
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
                if (UI.ServerApp.BackendTask is { IsCompleted: false } backend)
                {
                    RequestShutdown("desktop ended");
                    backend.GetAwaiter().GetResult();
                }
                Contract.SoltarElSitio();
            }
        }

        private static bool _headless;
        private static volatile bool _ready;
        private static volatile string _failure;
        internal static bool Ready => _ready;
        internal static string Failure => _failure;

        internal static async Task RunServerAsync()
        {
            try { await StartServicesAndWaitAsync(); }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                _failure = StartupFailure(ex);
                Console.WriteLine(Failure);
                LogFile.Debug.WriteLine(Failure);
            }
            finally
            {
                _ready = false;
                StopServices();
            }
            if (_restartRequested != 0 && Failure == null) StartTheNextRun();
        }

        /// <summary>The complete failure written before a WinExe exits during startup.</summary>
        internal static string StartupFailure(Exception error)
            => $"[!] Fatal error while starting Jondo Server:{Environment.NewLine}{error}";

        private static async Task StartServicesAndWaitAsync()
        {
            try { Console.Clear(); } catch { }
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("======================================================================");
            Console.WriteLine("                         JONDO — SERVER                              ");
            Console.WriteLine("======================================================================");
            Console.ResetColor();

            // A task let go without being awaited -- a clock, a background release, a send -- that
            // fails takes its exception with it, and some twenty of them are started that way. They
            // reach the log here instead of vanishing.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Console.WriteLine($"[!] A background task failed: {e.Exception.GetBaseException().Message}");
                LogDebug(e.Exception.ToString());
                e.SetObserved();
            };

            // 0. Resolved data paths. Everything the emulator needs now lives inside its own
            //    folder; the root is derived from the assembly location, not hardcoded.
            Paths.LogResolvedPaths();
            // Freeze this run's settings before the desktop can save edits for the next run.
            _ = ServerSettings.Current;

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
            Managers.CosmeticCollection.Initialize();
            Managers.Wardrobe.Initialize();
            Managers.Outfits.Initialize();
            Managers.Titles.Initialize();
            Managers.Cosmetics.Initialize();
            Managers.EquipmentSkins.Initialize();
            Managers.KoliseoMaps.Initialize();
            // The jail's sentences still running, and the clock that ends them.
            Managers.Jail.Initialize();
            Managers.Dreams.Initialize();
            Managers.Merkasako.Initialize();
            Managers.Zaapis.Initialize();
            Managers.Transports.Initialize();
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
            Managers.DungeonExitRooms.Load();
            // After the quests: 259 achievements are earned by finishing one, and the catalogue is indexed
            // by quest when loaded.
            Managers.Achievements.Load();
            // Behind the quests as well: the Almanax offerings are quests, and the calendar finds
            // their giver in the quest catalogue.
            Managers.Almanax.Load();
            // And today's saint in the temple, before anyone walks the NPCs' maps.
            Managers.AlmanaxTemple.Prepare();
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
            if (IsShuttingDown) return;
            Console.WriteLine("[+] Starting services...");
            try
            {
                Console.WriteLine($"[+] Network binding: {ServerBinding.Description}.");
                if (ServerBinding.Public)
                {
                    Console.WriteLine("[!] Public binding does not encrypt traffic. Use it only on " +
                                      "a trusted network or behind a VPN/tunnel.");
                }
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
                throw;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n[+] ALL EMULATION SERVICES ONLINE AND READY!");
            Console.ResetColor();

            // Here too one was invited to type /help. There is nowhere to: this is a WinExe with no
            // console and nobody reads standard input —not a single Console.ReadLine is left in the
            // server—. The real commands are those of the game chat, and
            // CommandHandler hands them out according to the role of whoever types them.

            // The launches that are left hanging.
            //
            // A client that starts and never gets to connect to 5555 —or a launcher that closes
            // right in the middle— left the account marked as busy FOREVER, and the registry
            // rejected it on every later attempt. CreatedAtUtc had been set from the start
            // without anyone reading it; now it is what releases them.
            using var launchCleanup = new System.Threading.Timer(
                _ => { try { Network.ClientLaunchRegistry.SoltarLosCaducados(TimeSpan.FromMinutes(5)); } catch { } },
                null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            // The Koliseo's queues, looked at again every few seconds: the rating window widens
            // with the wait, so a match that was not possible at enrolment may be now.
            using var matchmaking = new System.Threading.Timer(
                _ =>
                {
                    if (Interlocked.Exchange(ref _koliseoTicking, 1) != 0) return;
                    try { Handlers.KoliseoHandler.TickAsync().GetAwaiter().GetResult(); }
                    catch (Exception ex) { Console.WriteLine($"[Koliseo] The queue tick failed: {ex.Message}"); }
                    finally { Interlocked.Exchange(ref _koliseoTicking, 0); }
                },
                null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            // The prisms' clock: weakened ones turning vulnerable, the conquests' minutes, the
            // pacified going back to normal (Handlers.ConquestHandler).
            using var conquests = new System.Threading.Timer(
                _ =>
                {
                    if (Interlocked.Exchange(ref _conquestTicking, 1) != 0) return;
                    try { Handlers.ConquestHandler.TickAsync().GetAwaiter().GetResult(); }
                    catch (Exception ex) { Console.WriteLine($"[Conquest] The clock failed: {ex.Message}"); }
                    finally { Interlocked.Exchange(ref _conquestTicking, 0); }
                },
                null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            // Osavora's seasons, one a week (OsavoraSeasonHandler).
            Handlers.OsavoraSeasonHandler.Start();

            // The simulated players, when the settings ask for them: they come in through the
            // ports just opened, as clients do (Bots.SimulatedPlayers).
            Bots.SimulatedPlayers.Start(ServerSettings.Current);

            // The head hunters' clock: hunts opening and closing, the prey sighted (HeadHunts).
            var headHunts = new System.Threading.Timer(
                _ =>
                {
                    if (Interlocked.Exchange(ref _headHuntsTicking, 1) != 0) return;
                    try { Managers.HeadHunts.TickAsync().GetAwaiter().GetResult(); }
                    catch (Exception ex) { Console.WriteLine($"[Head hunts] The clock failed: {ex.Message}"); }
                    finally { Interlocked.Exchange(ref _headHuntsTicking, 0); }
                },
                null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            _ready = true;

            await _shutdown.Task;
            await launchCleanup.DisposeAsync();
            await matchmaking.DisposeAsync();
            await conquests.DisposeAsync();
            await headHunts.DisposeAsync();

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
        internal static Task ShutdownRequested => _shutdown.Task;

        /// <summary>Whether shutdown has already been asked for, so the window does not ask again.</summary>
        public static bool IsShuttingDown => Volatile.Read(ref _shutdownRequested) != 0;

        private static int _shutdownRequested;
        private static int _servicesStopped;

        /// <summary>
        /// Shuts down like <see cref="RequestShutdown"/> and starts the server again, which is how
        /// the settings saved from the window take effect.
        /// </summary>
        public static void RequestRestart(string reason)
        {
            Interlocked.Exchange(ref _restartRequested, 1);
            RequestShutdown(reason);
        }

        private static int _restartRequested;

        /// <summary>
        /// One Koliseo tick at a time: the timer fires every five seconds whatever the last tick is
        /// doing, and each one held a pool thread blocked behind the one before.
        /// </summary>
        private static int _koliseoTicking;
        private static int _conquestTicking;
        private static int _headHuntsTicking;

        /// <summary>The argument a restarted run gets: the process id of the run it replaces.</summary>
        private const string RestartAfter = "--restart-after";

        private static void StartTheNextRun()
        {
            try
            {
                var start = UI.ServerLaunch.Restart(Environment.ProcessPath!,
                    Environment.GetCommandLineArgs()[0], Environment.ProcessId, _headless);
                System.Diagnostics.Process.Start(start);
                Console.WriteLine("[+] The server starts again with its new settings.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] The server could not start again: {ex.Message}");
            }
        }

        /// <summary>Waits, for half a minute at most, for the run this one replaces to end.</summary>
        private static void WaitForThePreviousRun(string[] args)
        {
            int at = Array.IndexOf(args, RestartAfter);
            if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out int previous)) return;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(previous);
                process.WaitForExit(30_000);
            }
            catch (ArgumentException) { }       // already gone
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Requests a graceful shutdown of the emulator. The server window calls it when closing.
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
            try { Bots.SimulatedPlayers.Stop(); } catch { }
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
