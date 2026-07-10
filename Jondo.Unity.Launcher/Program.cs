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
            try { Console.Clear(); } catch { }
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("======================================================================");
            Console.WriteLine("                JONDO EMULATOR LAUNCHER (MODULAR C#)                  ");
            Console.WriteLine("======================================================================");
            Console.ResetColor();

            // 1. Initialize Database and Map Manager
            Console.WriteLine("[+] Initializing Database...");
            DatabaseManager.Initialize();

            Console.WriteLine("[+] Initializing Map Manager...");
            MapManager.Initialize();



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

            // 4. Automatically launch Dofus Client if present with injected environment variables
            string clientPath = @"C:\Jondo\DofusClient\Dofus.exe";
            if (File.Exists(clientPath))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[+] Launching Dofus Client from: {clientPath}...");
                Console.ResetColor();
                try
                {
                    string hash = Guid.NewGuid().ToString();
                    string argsStr = $"-force-d3d11 --port {port} --gameName dofus --gameRelease dofus3 --instanceId 1 --hash {hash} --canLogin true --langCode es --autoConnectType 1 --connectionPort {gamePort}";

                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = clientPath,
                        Arguments = argsStr,
                        WorkingDirectory = Path.GetDirectoryName(clientPath) ?? "",
                        UseShellExecute = false
                    };

                    startInfo.Environment["ZAAP_PORT"] = port.ToString();
                    startInfo.Environment["ZAAP_HASH"] = hash;
                    startInfo.Environment["ZAAP_GAME"] = "dofus";
                    startInfo.Environment["ZAAP_RELEASE"] = "dofus3";
                    startInfo.Environment["ZAAP_INSTANCE_ID"] = "1";
                    startInfo.Environment["ZAAP_CAN_AUTH"] = "true";

                    System.Diagnostics.Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[-] Failed to start Dofus Client: {ex.Message}");
                }
            }

            // 5. Interactive Console Command Loop
            bool running = true;
            while (running)
            {
                Console.Write("jondo> ");
                string? input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input)) continue;

                string[] tokens = input.Split(' ');
                string cmd = tokens[0].ToLower();

                switch (cmd)
                {
                    case "/help":
                        Console.WriteLine("Available commands:");
                        Console.WriteLine("  /status                    - Displays active character identity and stats.");
                        Console.WriteLine("  /teleport <mapId> <cellId> - Updates character position (saves to database).");
                        Console.WriteLine("  /exit or /quit             - Shuts down all servers and exits.");
                        break;

                    case "/status":
                        Console.WriteLine($"Active Character: {GameState.CharacterName} (ID: {GameState.CharacterId})");
                        Console.WriteLine($"Level: {GameState.CharacterLevel} | Breed: {GameState.Breed} | Sex: {GameState.Sex}");
                        Console.WriteLine($"Position: Map ID {GameState.MapId} | Cell ID {GameState.CellId}");
                        Console.WriteLine($"Characteristics: Points={GameState.CharacterRemainingPoints} | Vit={GameState.StatVitality} | Str={GameState.StatStrength} | Int={GameState.StatIntelligence} | Agi={GameState.StatAgility} | Wis={GameState.StatWisdom} | Cha={GameState.StatChance}");
                        break;

                    case "/teleport":
                        if (tokens.Length < 3 || !long.TryParse(tokens[1], out long mapId) || !int.TryParse(tokens[2], out int cellId))
                        {
                            Console.WriteLine("Usage: /teleport <mapId> <cellId>");
                            break;
                        }
                        GameState.MapId = mapId;
                        GameState.CellId = cellId;
                        DatabaseManager.SaveCurrentCharacter();
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[Teleport] Character teleported to Map {mapId}, Cell {cellId}. Saved to database.");
                        Console.ResetColor();
                        break;

                    case "/exit":
                    case "/quit":
                        Console.WriteLine("[+] Shutting down servers...");
                        HaapiServer.Stop();
                        ZaapServer.Stop();
                        GameServerProxy.Stop();
                        GameNodeProxy.Stop();
                        ChatServer.Stop();
                        running = false;
                        break;

                    default:
                        Console.WriteLine("Unknown command. Type /help for assistance.");
                        break;
                }
            }

            Console.WriteLine("[+] Shutdown complete. Goodbye!");
        }

        public static void LogDebug(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);
            lock (LogLock)
            {
                try
                {
                    File.AppendAllText(@"C:\Jondo\emulator_debug.log", line + "\r\n");
                }
                catch { }
            }
        }
    }
}
