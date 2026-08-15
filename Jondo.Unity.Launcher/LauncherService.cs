using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// Launcher logic, independent of the user interface.
    ///
    /// It used to live inside <see cref="HaapiServer"/> and could only be reached over HTTP from
    /// the web interface. The native desktop window now calls these methods directly, so the HTTP
    /// route in front of them has been removed: the only thing still speaking HTTP to that server
    /// is the Dofus client, and it does not use any of this.
    /// </summary>
    public static class LauncherService
    {
        /// <summary>Emulator version published in the service status.</summary>
        public const string Version = "3.6.10.10";

        /// <summary>Address used as the origin when the request comes from this very machine.</summary>
        public const string LocalIp = "127.0.0.1";

        // ─── Result types ───────────────────────────────────────────────────────

        /// <summary>Generic result of a launcher operation.</summary>
        public class Result
        {
            public bool Success { get; set; }
            public string Message { get; set; } = "";
        }

        /// <summary>Result of a successful login.</summary>
        public sealed class SignInResult : Result
        {
            public string Token { get; set; } = "";
            public string Nickname { get; set; } = "";
            public long AccountId { get; set; }
        }

        /// <summary>Status of the emulation services.</summary>
        public sealed class ServicesStatus
        {
            public bool Online { get; set; }
            public bool DatabaseOk { get; set; }
            public bool ServicesListening { get; set; }
            public string Version { get; set; } = LauncherService.Version;
        }

        /// <summary>A single line of the server event log.</summary>
        public sealed class LogEntry
        {
            public long Id { get; set; }
            public string Time { get; set; } = "";
            public string Message { get; set; } = "";
        }

        // ─── Operations ─────────────────────────────────────────────────────────

        /// <summary>
        /// Validates an account's credentials, generates its game token and makes it the active
        /// account, so that the HAAPI responses the client asks for return its data.
        /// </summary>
        public static SignInResult SignIn(string username, string password, string clientIp)
        {
            try
            {
                if (DatabaseManager.ValidateAccountCredentials(username, password, clientIp, out var account, out string error) && account != null)
                {
                    HaapiServer.ActiveAccount = account;
                    string token = Guid.NewGuid().ToString("N");
                    DatabaseManager.SetGameToken(account.Id, token);

                    return new SignInResult
                    {
                        Success = true,
                        Token = token,
                        Nickname = account.Nickname,
                        AccountId = account.Id
                    };
                }

                return new SignInResult { Success = false, Message = error };
            }
            catch (Exception ex)
            {
                return new SignInResult { Success = false, Message = $"Error processing the request: {ex.Message}" };
            }
        }

        /// <summary>Creates a new account with its nickname.</summary>
        public static Result RegisterAccount(string username, string password, string nickname, string clientIp)
        {
            try
            {
                if (DatabaseManager.RegisterNewAccount(username, password, nickname, clientIp, out string error))
                {
                    return new Result { Success = true, Message = "Account created successfully." };
                }

                return new Result { Success = false, Message = error };
            }
            catch (Exception ex)
            {
                return new Result { Success = false, Message = $"Error registering the account: {ex.Message}" };
            }
        }

        /// <summary>
        /// Starts the Dofus client executable, pointing it at the local emulator.
        /// The token identifies the account that has just logged in; if it is not recognized we
        /// fall back to the active account.
        /// </summary>
        public static Result LaunchClient(string token)
        {
            try
            {
                long accountId = DatabaseManager.GetAccountIdByToken(token ?? "");
                if (accountId <= 0)
                {
                    accountId = HaapiServer.ActiveAccount?.Id ?? 1;
                }

                string clientPath = ResolveClient();
                if (clientPath.Length == 0)
                {
                    return new Result
                    {
                        Success = false,
                        Message = "No se encuentra Dofus.exe. Elige dónde está con el botón de la ruta del cliente."
                    };
                }

                string hash = Guid.NewGuid().ToString();

                // Unity is told the size up front. Maximizing afterwards is not enough on its own:
                // the client rebuilds its window when it moves between screens (server choice,
                // character choice, world) and reapplies its own saved resolution, so it drops out
                // of the maximized state and part of the interface ends up off screen.
                var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                           ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

                // MelonLoader abre su propia consola negra y su pantalla de arranque delante del
                // juego. Se le dice que no por línea de órdenes además de por Loader.cfg: la orden
                // manda sobre el fichero, así que da igual que alguien lo reescriba.
                string arguments =
                    $"-force-d3d11 -screen-fullscreen 0 -screen-width {area.Width} -screen-height {area.Height} " +
                    "--melonloader.hideconsole --melonloader.disablestartscreen " +
                    $"--port 15881 --gameName dofus --gameRelease dofus3 --instanceId 1 --hash {hash} " +
                    $"--canLogin true --langCode {UI.LauncherTexts.Code(UI.LauncherPreferences.Language)} " +
                    "--autoConnectType 1 --connectionPort 5555";

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = clientPath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(clientPath) ?? "",
                    UseShellExecute = false
                };

                startInfo.Environment["ZAAP_PORT"] = "15881";
                startInfo.Environment["ZAAP_HASH"] = hash;
                startInfo.Environment["ZAAP_GAME"] = "dofus";
                startInfo.Environment["ZAAP_RELEASE"] = "dofus3";
                startInfo.Environment["ZAAP_INSTANCE_ID"] = "1";
                startInfo.Environment["ZAAP_CAN_AUTH"] = "true";

                var client = System.Diagnostics.Process.Start(startInfo);
                if (client != null) MaximizeWhenReady(client);
                return new Result { Success = true };
            }
            catch (Exception ex)
            {
                return new Result { Success = false, Message = $"Error starting the client: {ex.Message}" };
            }
        }

        /// <summary>
        /// Dónde está el Dofus.exe, o cadena vacía si no aparece.
        ///
        /// Manda lo que se haya elegido a mano, porque el cliente no tiene por qué estar junto al
        /// emulador: quien lo tenga en otro disco lo señala una vez y se acabó. Si no hay nada
        /// elegido —o lo elegido ya no existe— se busca donde se ha buscado siempre, al lado.
        /// </summary>
        public static string ResolveClient()
        {
            string elegido = UI.LauncherPreferences.ClientExecutable;
            if (elegido.Length > 0) return elegido;

            string alLado = Path.Combine(Paths.ClientDir, "Dofus.exe");
            return File.Exists(alLado) ? alLado : "";
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        private const int ShowMaximized = 3;

        /// <summary>
        /// Maximizes the client once it has a window. It opens at whatever size Unity has saved,
        /// which is often smaller than the screen, and the game lays its interface out against
        /// the window: part of the bottom bar ends up off the visible area.
        ///
        /// It has to be done from here rather than through a launch argument because the window
        /// does not exist yet when the process starts.
        /// </summary>
        private static void MaximizeWhenReady(System.Diagnostics.Process client)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(90);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (client.HasExited) return;
                        client.Refresh();
                        if (client.MainWindowHandle != IntPtr.Zero)
                        {
                            // The window appears before Unity finishes sizing it; maximizing too
                            // early gets undone.
                            await System.Threading.Tasks.Task.Delay(2500);
                            client.Refresh();
                            if (!client.HasExited && client.MainWindowHandle != IntPtr.Zero)
                            {
                                ShowWindow(client.MainWindowHandle, ShowMaximized);
                                Console.WriteLine("[Launcher] Client window maximized.");
                            }
                            return;
                        }
                        await System.Threading.Tasks.Task.Delay(500);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Launcher] Could not maximize the client window: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Checks that the databases exist and that the game servers are listening.
        /// </summary>
        public static ServicesStatus GetStatus()
        {
            bool databaseOk = File.Exists(Paths.AuthDb) && File.Exists(Paths.WorldDb);
            bool listening = ZaapServer.IsRunning && GameServerProxy.IsRunning;

            return new ServicesStatus
            {
                DatabaseOk = databaseOk,
                ServicesListening = listening,
                Online = databaseOk && listening,
                Version = Version
            };
        }

        /// <summary>
        /// Returns the console lines newer than the given id, already deserialized, so that the
        /// native window does not have to talk over HTTP with its own process.
        /// </summary>
        public static IReadOnlyList<LogEntry> GetLogs(long sinceId)
        {
            var entries = new List<LogEntry>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(ConsoleLogBuffer.GetLogsJson(sinceId));
                if (!doc.RootElement.TryGetProperty("logs", out var list)) return entries;

                foreach (var element in list.EnumerateArray())
                {
                    // Each line is read on its own. One that cannot be read must not carry off the
                    // ones behind it: the window only moves its cursor as far as what it is given,
                    // so a line that always fails would be asked for again for ever and the console
                    // would sit there frozen. Better to lose the line and keep going.
                    long entryId = 0;
                    try
                    {
                        entryId = element.TryGetProperty("id", out var id) ? id.GetInt64() : 0;
                        entries.Add(new LogEntry
                        {
                            Id = entryId,
                            Time = element.TryGetProperty("time", out var time) ? time.GetString() ?? "" : "",
                            Message = element.TryGetProperty("msg", out var msg) ? msg.GetString() ?? "" : ""
                        });
                    }
                    catch
                    {
                        if (entryId > 0)
                        {
                            entries.Add(new LogEntry
                            {
                                Id = entryId,
                                Time = "",
                                Message = "[log] a line could not be read and was dropped."
                            });
                        }
                    }
                }
            }
            catch
            {
                // A failure while reading the buffer must not take the UI down: it is retried on the next cycle.
            }
            return entries;
        }

    }
}
